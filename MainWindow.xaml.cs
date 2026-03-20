// ═══════════════════════════════════════════════════════════════════════════
//  CIPHER — MainWindow
//  Real-time E2E encrypted chat. All sensitive work is in Core.cs.
//  This file handles: UI state, message rendering, lazy history,
//  scroll management, contact management, nuke, stay-logged-in.
// ═══════════════════════════════════════════════════════════════════════════
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Forms = System.Windows.Forms;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Cipher;

// ── Brush cache — avoids allocating new brushes on every property access ──────
static class B {
    static readonly BrushConverter Cvt = new();
    static readonly Dictionary<string, SolidColorBrush> FallbackCache = [];
    static SolidColorBrush Make(string hex) {
        var b = (SolidColorBrush)Cvt.ConvertFromString(hex)!;
        b.Freeze(); // Frozen = thread-safe + GC-friendly
        return b;
    }
    static SolidColorBrush Resource(string key, string fallback) {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush) return brush;
        if (FallbackCache.TryGetValue(key, out var cached)) return cached;

        cached = Make(fallback);
        FallbackCache[key] = cached;
        return cached;
    }

    public static SolidColorBrush Green => Resource("Green", "#6EE7C8");
    public static SolidColorBrush DimGreen => Resource("DimGreen", "#3BAF93");
    public static SolidColorBrush Amber => Resource("Amber", "#F6B15F");
    public static SolidColorBrush AmberDim => Resource("AmberDim", "#E09A3C");
    public static SolidColorBrush AmberText => Resource("AmberText", "#FFD8A6");
    public static SolidColorBrush TextGreen => Resource("TextGreen", "#D8FFF5");
    public static SolidColorBrush MessageText => Resource("MessageText", "#D8E8F4");
    public static SolidColorBrush Dim => Resource("Dim", "#7C8B9F");
    public static SolidColorBrush DimName => Resource("DimName", "#B9C6D6");
    public static SolidColorBrush DimOnline => Resource("DimOnline", "#5E6F86");
    public static SolidColorBrush NameOnline => Resource("NameOnline", "#EEF5FF");
    public static SolidColorBrush Red => Resource("Red", "#FF6B6B");
    public static SolidColorBrush Blue => Resource("Blue", "#7AA2FF");
    public static SolidColorBrush MineBubble => Resource("BubbleMine", "#B82A2444");
    public static SolidColorBrush PeerBubble => Resource("BubbleTheirs", "#A016333A");
    public static SolidColorBrush BubbleBorderMine => Resource("BubbleBorderMine", "#306EE7C8");
    public static SolidColorBrush BubbleBorderTheirs => Resource("BubbleBorderTheirs", "#2558D7B6");
    public static SolidColorBrush SeenChipBg => Resource("SeenChipBg", "#213042");
    public static SolidColorBrush Transparent => Resource("Transparent", "#00000000");
}

// ── View Models ──────────────────────────────────────────────────────────────

public class ConvViewModel : INotifyPropertyChanged {
    static readonly FontFamily ConversationIconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily PresenceFont = new("Segoe UI Symbol, Segoe UI Emoji, Segoe UI");

    string _displayName = "";
    bool _isOnline;
    int _unreadCount;
    bool _isGroup;
    bool _isMuted;
    string _lastMessagePreview = "No messages yet";
    long _lastMessageTimestamp;

    public string Id { get; set; } = "";
    public string DisplayName {
        get => _displayName;
        set { _displayName = value; Notify(); Notify(nameof(NameBrush)); }
    }
    public bool IsOnline {
        get => _isOnline;
        set { _isOnline = value; Notify(); Notify(nameof(StatusChar)); Notify(nameof(StatusBrush)); Notify(nameof(NameBrush)); }
    }
    public int UnreadCount {
        get => _unreadCount;
        set { _unreadCount = value; Notify(); Notify(nameof(HasUnread)); Notify(nameof(UnreadLabel)); }
    }
    public bool IsGroup {
        get => _isGroup;
        set {
            _isGroup = value;
            Notify();
            Notify(nameof(ConversationGlyph));
            Notify(nameof(ConversationGlyphBrush));
            Notify(nameof(ConversationGlyphFontFamily));
            Notify(nameof(ConversationGlyphFontSize));
            Notify(nameof(ShowGroupBadge));
        }
    }
    public bool IsMuted {
        get => _isMuted;
        set { _isMuted = value; Notify(); Notify(nameof(ShowMutedBadge)); }
    }
    public string LastMessagePreview {
        get => _lastMessagePreview;
        set { _lastMessagePreview = value; Notify(); }
    }
    public long LastMessageTimestamp {
        get => _lastMessageTimestamp;
        set { _lastMessageTimestamp = value; Notify(); Notify(nameof(LastMessageTime)); Notify(nameof(HasLastMessageTime)); }
    }

    public string StatusChar => "●";
    public Brush StatusBrush => IsOnline ? B.Green : B.DimOnline;
    public Brush NameBrush => IsOnline ? B.NameOnline : B.DimName;
    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString(CultureInfo.InvariantCulture);
    public string ConversationGlyph => IsGroup ? "\uE716" : StatusChar;
    public Brush ConversationGlyphBrush => IsGroup ? B.Amber : StatusBrush;
    public FontFamily ConversationGlyphFontFamily => IsGroup ? ConversationIconFont : PresenceFont;
    public double ConversationGlyphFontSize => IsGroup ? 13 : 10;
    public bool ShowGroupBadge => IsGroup;
    public string GroupGlyph => "\uE716";
    public Brush GroupGlyphBrush => B.Dim;
    public bool ShowMutedBadge => IsMuted;
    public bool HasLastMessageTime => LastMessageTimestamp > 0;
    public string LastMessageTime => LastMessageTimestamp <= 0 ? "" : FormatConversationTimestamp(LastMessageTimestamp);

    public Contact?   ContactData { get; set; }
    public GroupInfo? GroupData   { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new(p));

    static string FormatConversationTimestamp(long timestamp) {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime;
        var today = DateTime.Now.Date;
        if (local.Date == today)
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date >= today.AddDays(-6))
            return local.ToString("ddd", CultureInfo.CurrentCulture);
        return local.ToString("dd/MM", CultureInfo.CurrentCulture);
    }
}

public class MessageViewModel : INotifyPropertyChanged {
    MessageStatus _status;
    string _deliveryText = "";
    Brush _deliveryBrush = B.Dim;
    Brush _senderColorBrush = B.TextGreen;

    public MessageViewModel() {
        SeenBy.CollectionChanged += (_, _) => Notify(nameof(HasSeenBy));
    }

    public string Id             { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SenderId       { get; set; } = "";
    public string SenderLabel    { get; set; } = "";
    public string Content        { get; set; } = "";
    public long   Timestamp      { get; set; }
    public long   SeqNum         { get; set; }
    public bool   IsMine         { get; set; }
    public bool   IsGroup        { get; set; }
    public bool   UseBubblelessLayout { get; set; }
    public ObservableCollection<SeenByChipViewModel> SeenBy { get; } = [];
    public MessageStatus Status {
        get => _status;
        set { _status = value; Notify(); Notify(nameof(StatusChar)); Notify(nameof(StatusBrush)); Notify(nameof(BubbleBorderBrush)); }
    }
    public string DeliveryText {
        get => _deliveryText;
        set { _deliveryText = value; Notify(); Notify(nameof(HasDeliveryText)); }
    }
    public Brush DeliveryBrush {
        get => _deliveryBrush;
        set { _deliveryBrush = value; Notify(); }
    }
    public Brush SenderColorBrush {
        get => _senderColorBrush;
        set { _senderColorBrush = value; Notify(); Notify(nameof(NameBrush)); }
    }

    public string TimeStr    => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp)
                                              .LocalDateTime.ToString("HH:mm");
    public Brush  TextBrush   => B.MessageText;
    public Brush  NameBrush   => SenderColorBrush;
    public Brush  RowBg       => B.Transparent;
    public Brush  BubbleBackground => UseBubblelessLayout
        ? B.Transparent
        : IsMine ? B.MineBubble : B.PeerBubble;
    public Brush  BubbleBorderBrush => UseBubblelessLayout
        ? B.Transparent
        : IsMine ? B.BubbleBorderMine : B.BubbleBorderTheirs;
    public Thickness BubbleBorderThickness => UseBubblelessLayout ? new Thickness(0) : new Thickness(1);
    public Thickness BubblePadding => UseBubblelessLayout ? new Thickness(0) : new Thickness(6, 3, 6, 4);
    public CornerRadius BubbleCornerRadius => UseBubblelessLayout ? new CornerRadius(0) : new CornerRadius(10);
    public double BubbleMaxWidth => UseBubblelessLayout ? 360d : 400d;
    public Thickness MessageBodyMargin => UseBubblelessLayout ? new Thickness(0, 2, 0, 0) : new Thickness(0, 2, 0, 0);
    public Thickness DeliveryMargin => UseBubblelessLayout ? new Thickness(0, 4, 0, 0) : new Thickness(0, 4, 0, 0);
    public bool HasSeenBy => SeenBy.Count > 0;
    public bool HasDeliveryText => !string.IsNullOrWhiteSpace(DeliveryText);
    public string StatusChar  => IsMine ? Status switch {
        MessageStatus.Sending   => "◌",
        MessageStatus.Sent      => "◎",
        MessageStatus.Delivered => "●",
        MessageStatus.Failed    => "✗",
        _ => ""
    } : "";
    public Brush StatusBrush => Status switch {
        MessageStatus.Failed => B.Red,
        MessageStatus.Seen => B.Blue,
        MessageStatus.Delivered => B.Green,
        _ => B.Dim
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new(p));
}

// ── Main Window ───────────────────────────────────────────────────────────────

public sealed class SeenByChipViewModel {
    public string Name { get; init; } = "";
    public Brush Color { get; init; } = B.Dim;
}

public sealed class UnreadSeparatorViewModel {
    public string Label { get; init; } = "new messages";
}

enum ToastSeverity {
    Info,
    Warning,
    Error
}

public sealed class ToastViewModel {
    public string Message { get; init; } = "";
    public Brush BackgroundBrush { get; init; } = B.Transparent;
    public Brush BorderBrush { get; init; } = B.Transparent;
    public Brush ForegroundBrush { get; init; } = B.MessageText;
    public Brush DotBrush { get; init; } = B.Green;
    public bool CanDismiss { get; init; }
    public string Category { get; init; } = "";
}

sealed class GroupInvitePayload {
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string GroupKey { get; set; } = "";
    public List<string> MemberIds { get; set; } = [];
    public string OwnerId { get; set; } = "";
}

sealed class GroupDeletePayload {
    public string GroupId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
}

record DeliveryReceiptPayload(string MessageId, string ConversationId, string Scope, string Status, long Ts);

public partial class MainWindow : Window {
    const string GroupInvitePrefix = "[cipher-group-invite]";
    const string GroupDeletePrefix = "[cipher-group-delete]";
    const string DeliveryReceiptPrefix = "[cipher-receipt]";
    const string ReceiptScopeDirect = "dm";
    const string ReceiptScopeGroup = "grp";
    const string ReceiptStatusReceived = "received";
    const string ReceiptStatusSeen = "seen";

    // ── State ─────────────────────────────────────────────────────────────
    Vault _vault = new();
    NetworkClient _net = new();
    LocalUser? _user;

    readonly ObservableCollection<ConvViewModel> _convs = [];
    readonly ObservableCollection<object> _messages = [];
    readonly ObservableCollection<ToastViewModel> _toasts = [];

    ConvViewModel? _activeConv;
    string _activeConvId = "";

    // Lazy loading state
    int _msgOffset = 0;
    int _msgTotal = 0;
    bool _loadingHistory = false;
    bool _isNearBottom = true;
    ScrollViewer? _msgScrollViewer;
    ScrollViewer? _inputScrollViewer;

    // Per-conversation shared secrets (cached in memory, not re-derived every message)
    readonly Dictionary<string, byte[]> _convKeys = [];

    // Sequence number tracking per sender per conversation (replay protection on client)
    readonly Dictionary<string, Dictionary<string, long>> _seqTracker = [];
    readonly Dictionary<string, long> _seqTrackerTouched = [];
    readonly Dictionary<string, HashSet<string>> _messageReceivedBy = [];
    readonly Dictionary<string, HashSet<string>> _messageSeenBy = [];
    readonly Queue<string> _receiptMessageOrder = [];
    readonly HashSet<string> _receiptMessageIds = [];
    readonly Dictionary<string, Brush> _userColorCache = [];
    readonly HashSet<string> _sentReceiptKeys = [];
    readonly Queue<string> _sentReceiptOrder = [];
    const string ConversationMuteSettingPrefix = "conv-muted:";
    const string UiThemeSettingKey = "ui-theme";
    const string UiChatFontSizeSettingKey = "ui-chat-font-size";
    const string DefaultThemeFile = "Theme.Teal.xaml";
    const double DefaultChatFontSize = 17d;
    const int MaxSeqTrackerConversations = 512;
    const int MaxReceiptCacheMessages = 5_000;
    const int MaxSentReceiptKeys = 10_000;

    // Reconnect outbox flush timer
    DispatcherTimer? _outboxTimer;
    DispatcherTimer? _typingIndicatorHideTimer;
    DispatcherTimer? _typingIndicatorPulseTimer;
    bool _networkEventsWired;
    bool _flushingOutbox;
    bool _groupInviteMode;
    int _typingIndicatorDotCount;
    string _activeThemeFile = DefaultThemeFile;
    AppShellPreferences _shellPreferences = new();
    Forms.NotifyIcon? _notifyIcon;
    bool _applyingShellPreferences;
    bool _exitRequested;
    bool _trayCloseHintShown;
    IEnumerable<MessageViewModel> MessageItems => _messages.OfType<MessageViewModel>();

    // ── Init ──────────────────────────────────────────────────────────────

    public MainWindow() {
        InitializeComponent();
        AppLog.Info("ui", "main window initialized");
        InitializeShellPreferences();
        ApplyTheme(_shellPreferences.ThemeFile, persist: false);
        ApplyChatFontSize(_shellPreferences.ChatFontSize, persist: false);
        ApplyBranding();
        ApplyUxPolish();
        InitializeDesktopNotifications();
        InitializeTestingFeatures();
        MessageList.ItemsSource = _messages;
        ToastHost.ItemsSource = _toasts;
        ConvList.ItemsSource = _convs;
        LoginRememberSession.IsChecked = Session.HasSession();
        RegisterRememberSession.IsChecked = false;
        InitializeReleaseFeatures();
        InitializeEmojiPicker();
        InitializeTypingIndicatorTimers();
        SourceInitialized += (_, _) => WindowsCompositionHelper.TryApplyMica(this);

        // Hook scroll event for lazy loading (set after list renders)
        MessageList.Loaded += (_, _) => {
            _msgScrollViewer = GetScrollViewer(MessageList);
            if (_msgScrollViewer != null)
                _msgScrollViewer.ScrollChanged += OnScrollChanged;
        };
        InputBox.Loaded += (_, _) => InitializeComposerPreview();

        // Outbox retry timer: every 5 seconds try to flush pending messages
        _outboxTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _outboxTimer.Tick += (_, _) => RunUiTask(FlushOutboxAsync, "flush outbox", showSidebarErrors: false);
        _outboxTimer.Start();

        // Try auto-login if session saved
        Loaded += (_, _) => RunUiTask(TryAutoLoginAsync, "startup auto-login", showSidebarErrors: false);
        Loaded += (_, _) => ApplyStartupTrayLaunch();

        SetConversationSurfaceState(false);

        if (!App.LastStartupHealth.IsHealthy) {
            SetAuthStatus(App.LastStartupHealth.Errors[0], true);
        } else if (App.LastStartupHealth.Warnings.Count > 0) {
            SetAuthStatus(App.LastStartupHealth.Warnings[0], false);
        }
    }

    void ApplyBranding() {
        Title = AppBranding.WindowTitle;
        if (AuthBrandTitle != null) AuthBrandTitle.Text = AppBranding.WindowTitle;
        if (HeaderBrandText != null) HeaderBrandText.Text = AppBranding.WindowTitle;
        if (VaultStorageHintText != null) VaultStorageHintText.Text = AppBranding.VaultStorageHint;
    }

    void InitializeShellPreferences() {
        var hadStoredPreferences = AppShellPreferencesStore.Exists();
        var stored = AppShellPreferencesStore.Load();
        var startup = WindowsStartupManager.ReadStatus();

        if (!hadStoredPreferences && !startup.IsEnabled && !AppRuntime.IsTestMode) {
            try {
                var initialDefaults = AppShellPreferencesStore.Sanitize(stored);
                WindowsStartupManager.Apply(initialDefaults);
                AppShellPreferencesStore.Save(initialDefaults);
                startup = WindowsStartupManager.ReadStatus();
            } catch (Exception ex) {
                AppLog.Warn("settings", $"failed to apply default startup behavior: {ex.Message}");
            }
        }

        _shellPreferences = AppShellPreferencesStore.Sanitize(stored with {
            StartWithWindows = startup.IsEnabled,
            StartHiddenOnStartup = startup.IsEnabled ? startup.StartHidden : stored.StartHiddenOnStartup,
            StartupDelaySeconds = startup.IsEnabled && startup.StartupDelaySeconds >= 0
                ? startup.StartupDelaySeconds
                : stored.StartupDelaySeconds
        });

        if (!hadStoredPreferences) {
            try {
                AppShellPreferencesStore.Save(_shellPreferences);
            } catch (Exception ex) {
                AppLog.Warn("settings", $"failed to persist initial shell preferences: {ex.Message}");
            }
        }

        ApplyShellPreferencesToUi();
    }

    void ApplyShellPreferencesToUi() {
        _applyingShellPreferences = true;
        try {
            ChkCloseToTrayOnClose.IsChecked = _shellPreferences.CloseToTrayOnClose;
            ChkStartWithWindows.IsChecked = _shellPreferences.StartWithWindows;
            ChkStartHiddenOnStartup.IsChecked = _shellPreferences.StartHiddenOnStartup;
            UpdateStartupDelayButtonStates();
            UpdateStartupSettingsEnabledState();
        } finally {
            _applyingShellPreferences = false;
        }

        _settingsWindow?.ApplyShellPreferences(_shellPreferences);
    }

    void UpdateStartupSettingsEnabledState() {
        var enabled = _shellPreferences.StartWithWindows;
        ChkStartHiddenOnStartup.IsEnabled = enabled;
        StartupDelayPanel.IsEnabled = enabled;
        SettingsStartupHintText.Opacity = enabled ? 1.0 : 0.7;
    }

    void UpdateStartupDelayButtonStates() {
        UpdateStartupDelayButton(BtnStartupDelay0, 0, "Off");
        UpdateStartupDelayButton(BtnStartupDelay15, 15, "15 sec");
        UpdateStartupDelayButton(BtnStartupDelay30, 30, "30 sec");
        UpdateStartupDelayButton(BtnStartupDelay60, 60, "60 sec");
    }

    void UpdateStartupDelayButton(Button button, int seconds, string label) {
        var active = _shellPreferences.StartupDelaySeconds == seconds;
        button.Style = (Style)FindResource(active ? "AccentBtn" : "TermBtn");
        button.Content = active ? $"✓ {label}" : label;
    }

    bool TryApplyShellPreferences(AppShellPreferences next, string successMessage, bool syncWindowsStartup) {
        var previous = _shellPreferences;
        next = AppShellPreferencesStore.Sanitize(next);

        try {
            if (syncWindowsStartup) {
                WindowsStartupManager.Apply(next);
            }

            AppShellPreferencesStore.Save(next);
            _shellPreferences = next;
            ApplyShellPreferencesToUi();
            RefreshDiagnosticsSummary();
            RefreshSettingsWindowState();
            SetSidebarStatus(successMessage);
            return true;
        } catch (Exception ex) {
            _shellPreferences = previous;
            ApplyShellPreferencesToUi();
            AppLog.Warn("settings", $"failed to apply shell preferences: {ex.Message}");
            SetSidebarStatus($"! failed to update app behavior: {ex.Message}");
            return false;
        }
    }

    string DescribeCloseBehavior() =>
        _shellPreferences.CloseToTrayOnClose ? "tray" : "exit";

    string DescribeStartupBehavior() {
        var startup = WindowsStartupManager.ReadStatus();
        if (!startup.IsEnabled) return "off";

        var mode = startup.StartHidden ? "tray" : "window";
        var delay = startup.StartupDelaySeconds <= 0
            ? "no delay"
            : $"{startup.StartupDelaySeconds}s delay";
        return $"on ({mode}, {delay})";
    }

    void ChkCloseToTrayOnClose_Click(object s, RoutedEventArgs e) {
        if (_applyingShellPreferences) return;
        UpdateCloseToTrayPreference(ChkCloseToTrayOnClose.IsChecked == true);
    }

    void ChkStartWithWindows_Click(object s, RoutedEventArgs e) {
        if (_applyingShellPreferences) return;
        UpdateStartWithWindowsPreference(ChkStartWithWindows.IsChecked == true);
    }

    void ChkStartHiddenOnStartup_Click(object s, RoutedEventArgs e) {
        if (_applyingShellPreferences) return;
        UpdateStartHiddenPreference(ChkStartHiddenOnStartup.IsChecked == true);
    }

    void StartupDelayButton_Click(object s, RoutedEventArgs e) {
        if (_applyingShellPreferences || s is not Button { Tag: string tag }) return;
        if (!int.TryParse(tag, out var seconds)) return;
        UpdateStartupDelayPreference(seconds);
    }

    void ApplyStartupTrayLaunch() {
        if (!AppRuntime.StartHidden || AppRuntime.IsTestMode) return;

        Dispatcher.BeginInvoke(() => HideToTray(showHint: false), DispatcherPriority.ApplicationIdle);
    }

    void ApplyUxPolish() {
        BtnSecurityReview.Content = "Verify";
        BtnSecurityReview.Style = (Style)FindResource("GroupBtn");
        BtnOpenAddOverlay.Content = "Add Friend";
        BtnOpenAddOverlay.Style = (Style)FindResource("AccentBtn");
        BtnOpenAddOverlay.Visibility = Visibility.Collapsed;
        BtnGroupInvite.Content = "Invite Members";
        BtnGroupInvite.Style = (Style)FindResource("GroupBtn");
        BtnGroupInvite.Visibility = Visibility.Collapsed;
        BtnGroupMenu.Content = "...";
        BtnGroupMenu.Style = (Style)FindResource("InfoBtn");
        BtnMuteConversation.Content = "Mute";
        BtnMuteConversation.Style = (Style)FindResource("InfoBtn");
        BtnMuteConversation.Visibility = Visibility.Collapsed;
        BtnSettings.Content = "Settings";
        BtnSettings.Style = (Style)FindResource("InfoBtn");
        BtnMyId.Style = (Style)FindResource("InfoBtn");

        TabAddDm.Content = "Add Friend";
        TabAddDm.Style = (Style)FindResource("AccentBtn");
        TabAddGroup.Content = "New Group";
        TabAddGroup.Style = (Style)FindResource("GroupBtn");
    }

    void InitializeTypingIndicatorTimers() {
        _typingIndicatorHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _typingIndicatorHideTimer.Tick += (_, _) => HideTypingIndicator();

        _typingIndicatorPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _typingIndicatorPulseTimer.Tick += (_, _) => {
            if (TypingIndicator.Visibility != Visibility.Visible) return;
            _typingIndicatorDotCount = (_typingIndicatorDotCount % 3) + 1;
            TypingIndicator.Text = $"typing{new string('.', _typingIndicatorDotCount)}";
        };
    }

    void UpdateComposerState() {
        var canCompose = _activeConv != null && InputBox.IsEnabled;
        BtnEmojiPicker.IsEnabled = canCompose;
        BtnSend.IsEnabled = canCompose && !string.IsNullOrWhiteSpace(InputBox.Text);
        UpdateInputPlaceholderState();
        UpdateInputPreviewState();
    }

    void UpdateInputPlaceholderState() {
        if (InputPlaceholder == null || InputBox == null) return;

        InputPlaceholder.Text = _activeConv == null
            ? "Choose a conversation to start chatting"
            : "Type a message...";
        InputPlaceholder.Visibility = string.IsNullOrWhiteSpace(InputBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void InitializeComposerPreview() {
        if (InputBox == null) return;
        _inputScrollViewer = GetScrollViewer(InputBox);
        if (_inputScrollViewer != null) {
            _inputScrollViewer.ScrollChanged -= ComposerPreviewScrollChanged;
            _inputScrollViewer.ScrollChanged += ComposerPreviewScrollChanged;
        }

        UpdateInputPreviewState();
    }

    void ComposerPreviewScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (InputPreviewTransform != null) {
            InputPreviewTransform.Y = -e.VerticalOffset;
        }
    }

    void UpdateInputPreviewState() {
        if (InputBox == null || InputPreview == null) return;

        var hasEmoji = MsgBodyHelper.ContainsEmoji(InputBox.Text);
        var showPreview = hasEmoji && !string.IsNullOrWhiteSpace(InputBox.Text);
        InputPreview.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        InputBox.Foreground = showPreview
            ? System.Windows.Media.Brushes.Transparent
            : (Brush)FindResource("White");

        if (InputPreviewTransform != null) {
            InputPreviewTransform.Y = -(_inputScrollViewer?.VerticalOffset ?? 0);
        }
    }

    void StartTypingIndicator() {
        if (_activeConv == null || string.IsNullOrWhiteSpace(InputBox.Text)) {
            HideTypingIndicator();
            return;
        }

        _typingIndicatorDotCount = Math.Max(1, _typingIndicatorDotCount);
        TypingIndicator.Text = $"typing{new string('.', _typingIndicatorDotCount)}";
        TypingIndicator.Visibility = Visibility.Visible;
        _typingIndicatorHideTimer?.Stop();
        _typingIndicatorHideTimer?.Start();
        _typingIndicatorPulseTimer?.Start();
    }

    void HideTypingIndicator() {
        _typingIndicatorHideTimer?.Stop();
        _typingIndicatorPulseTimer?.Stop();
        _typingIndicatorDotCount = 0;
        TypingIndicator.Text = "";
        TypingIndicator.Visibility = Visibility.Collapsed;
    }

    static string ConversationMuteSettingKey(string conversationId) =>
        $"{ConversationMuteSettingPrefix}{conversationId}";

    bool IsConversationMuted(string conversationId) =>
        _vault.IsOpen &&
        string.Equals(_vault.GetSetting(ConversationMuteSettingKey(conversationId)), "1", StringComparison.Ordinal);

    void SetConversationMuted(ConvViewModel conv, bool isMuted) {
        conv.IsMuted = isMuted;
        if (_vault.IsOpen) {
            _vault.SetSetting(ConversationMuteSettingKey(conv.Id), isMuted ? "1" : "0");
        }
        RefreshMuteButton();
    }

    void RefreshMuteButton() {
        if (BtnMuteConversation == null) return;
        if (_activeConv == null) {
            BtnMuteConversation.Visibility = Visibility.Collapsed;
            return;
        }

        BtnMuteConversation.Visibility = Visibility.Visible;
        BtnMuteConversation.Content = _activeConv.IsMuted ? "Unmute" : "Mute";
        BtnMuteConversation.ToolTip = _activeConv.IsMuted
            ? "Allow desktop notifications for this chat"
            : "Silence desktop notifications for this chat";
        BtnMuteConversation.Style = (Style)FindResource(_activeConv.IsMuted ? "TermBtn" : "InfoBtn");
        UpdateComposerState();
    }

    void SetConversationSurfaceState(bool hasConversation) {
        if (ActiveConvName != null && !hasConversation)
            ActiveConvName.Text = "No conversation selected";
        if (ActiveConvName != null)
            ActiveConvName.Foreground = hasConversation
                ? (Brush)FindResource("White")
                : (Brush)FindResource("Muted");
        if (EmptyState != null)
            EmptyState.Visibility = hasConversation ? Visibility.Collapsed : Visibility.Visible;
        if (MessageList != null)
            MessageList.Visibility = hasConversation ? Visibility.Visible : Visibility.Collapsed;
        if (ConversationActionsBar != null)
            ConversationActionsBar.Visibility = hasConversation ? Visibility.Visible : Visibility.Collapsed;
        if (ComposerPanel != null)
            ComposerPanel.Visibility = Visibility.Visible;
        if (InputBox != null) {
            InputBox.IsEnabled = hasConversation;
        }
        if (!hasConversation)
            HideTypingIndicator();
        if (!hasConversation && EmojiPickerHost != null)
            EmojiPickerHost.Visibility = Visibility.Collapsed;
        if (!hasConversation && GroupMenuPopup != null)
            GroupMenuPopup.IsOpen = false;
        UpdateComposerState();
    }

    void InitializeEmojiPicker() {
        if (EmojiPanel == null) return;
        EmojiPanel.Children.Clear();

        foreach (var entry in OpenMojiCatalog.Entries) {
            var button = new Button {
                Width = 56,
                Height = 56,
                Margin = new Thickness(4),
                Style = (Style)FindResource("TermBtn"),
                ToolTip = $"{entry.Label} ({entry.Emoji})",
                Tag = entry
            };

            var stack = new StackPanel {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var image = new Image {
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 2)
            };
            try {
                image.Source = new BitmapImage(new Uri(entry.PackUri, UriKind.Absolute));
            } catch {
                image.Visibility = Visibility.Collapsed;
            }

            stack.Children.Add(image);

            button.Content = stack;
            button.Click += BtnEmojiSymbol_Click;
            EmojiPanel.Children.Add(button);
        }
    }

    void BtnEmojiPicker_Click(object s, RoutedEventArgs e) {
        if (_activeConv == null) {
            SetSidebarStatus("open a conversation first to send emojis");
            return;
        }
        EmojiPickerHost.Visibility = EmojiPickerHost.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    void BtnEmojiSymbol_Click(object? sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: OpenMojiEntry entry }) return;
        InsertEmoji(entry.Emoji);
    }

    void InsertEmoji(string emoji) {
        if (string.IsNullOrEmpty(emoji)) return;
        var caret = InputBox.CaretIndex;
        InputBox.Text = InputBox.Text.Insert(caret, emoji);
        InputBox.CaretIndex = caret + emoji.Length;
        InputBox.Focus();
    }

    // ── Auto-login ────────────────────────────────────────────────────────

    async Task TryAutoLoginAsync() {
        var started = AppTelemetry.StartTimer();
        AppLog.Info("auth", $"startup auth check - vault_exists={System.IO.File.Exists(Vault.DefaultVaultPath)} session_present={Session.HasSession()}");
        if (!System.IO.File.Exists(Vault.DefaultVaultPath)) {
            ShowRegisterTab();
            SetAuthStatus("", false);
            SignalAuthReady("register");
            AppLog.Info("auth", "no vault found - showing register screen");
            await RunTestStartupActionsAsync();
            return;
        }

        // Show login as default when vault exists
        ShowLoginTab();

        // Try DPAPI session key
        var sessionKey = Session.TryLoad();
        if (sessionKey != null) {
            SetAuthStatus("unlocking vault...", false);
            AppLog.Info("auth", "saved session found - attempting auto-login");
            try {
                _vault.Open(Vault.DefaultVaultPath, sessionKey);
                _user = _vault.LoadIdentity();
                if (_user != null) {
                    var normalizedServerUrl = RelayUrl.Normalize(_user.ServerUrl);
                    if (!string.Equals(_user.ServerUrl, normalizedServerUrl, StringComparison.Ordinal)) {
                        AppLog.Info("relay", $"migrated saved relay URL to {AppTelemetry.DescribeRelay(normalizedServerUrl)}");
                        _user.ServerUrl = normalizedServerUrl;
                        _vault.SaveIdentity(_user);
                    }
                    LoginServerUrl.Text = _user.ServerUrl;
                    ReportVaultMaintenance();
                    AppLog.Info("auth", $"auto-login succeeded for {AppTelemetry.MaskUserId(_user.UserId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
                    await StartChatAsync();
                    return;
                }
            } catch (Exception ex) {
                AppLog.Warn("auth", $"auto-login failed: {ex.Message}");
                Session.Clear();
                _vault.Dispose();
                _vault = new Vault();
            } finally {
                Crypto.Wipe(sessionKey);
            }
        }

        // Load saved server URL if vault exists
        var saltExists = System.IO.File.Exists(Vault.SaltPath);
        SetAuthStatus(saltExists ? "" : "no vault found — create one", false);
        if (!saltExists) {
            ShowRegisterTab();
            SignalAuthReady("register");
        } else {
            SignalAuthReady("login");
            AppLog.Info("auth", $"vault found without saved session - waiting for manual login after {AppTelemetry.ElapsedMilliseconds(started)}ms");
        }
        await RunTestStartupActionsAsync();
    }

    // ── Auth handlers ─────────────────────────────────────────────────────

    void TabLogin_Click(object s, RoutedEventArgs e) => ShowLoginTab();
    void TabRegister_Click(object s, RoutedEventArgs e) => ShowRegisterTab();

    void ShowLoginTab() {
        LoginForm.Visibility = Visibility.Visible;
        RegisterForm.Visibility = Visibility.Collapsed;
        TabLogin.BorderBrush = (Brush)FindResource("Green");
        TabRegister.BorderBrush = (Brush)FindResource("Border");
        if (string.IsNullOrEmpty(LoginServerUrl.Text))
            LoginServerUrl.Text = AppRuntime.EffectiveDefaultRelayUrl;
        SignalAuthReady("login");
    }

    void ShowRegisterTab() {
        LoginForm.Visibility = Visibility.Collapsed;
        RegisterForm.Visibility = Visibility.Visible;
        TabRegister.BorderBrush = (Brush)FindResource("Green");
        TabLogin.BorderBrush = (Brush)FindResource("Border");
        if (string.IsNullOrEmpty(RegServerUrl.Text))
            RegServerUrl.Text = AppRuntime.EffectiveDefaultRelayUrl;
        SignalAuthReady("register");
    }

    void LoginPass_KeyDown(object s, KeyEventArgs e) {
        if (e.Key == Key.Enter) BtnLogin_Click(s, e);
    }

    void BtnLogin_Click(object s, RoutedEventArgs e) =>
        RunUiTask(LoginAsync, "unlock vault", showSidebarErrors: false, onError: message => {
            SetAuthStatus(message, true);
            BtnLogin.IsEnabled = true;
        });

    async Task LoginAsync() {
        var started = AppTelemetry.StartTimer();
        var pass = LoginPass.Password;
        var serverUrl = RelayUrl.Normalize(LoginServerUrl.Text);
        if (string.IsNullOrEmpty(pass)) { SetAuthStatus("enter password", true); return; }
        if (!System.IO.File.Exists(Vault.SaltPath)) { SetAuthStatus("no vault found — create one", true); return; }

        if (!RelayUrl.IsValid(serverUrl)) { SetAuthStatus(RelayUrl.ValidationHint, true); return; }
        SetAuthStatus("deriving key (this takes ~2s)...", false);
        BtnLogin.IsEnabled = false;
        AppLog.Info("auth", $"manual login started - relay={AppTelemetry.DescribeRelay(serverUrl)}");

        try {
            var salt = await Task.Run(() => System.IO.File.ReadAllBytes(Vault.SaltPath));
            var key = await Task.Run(() => Crypto.DeriveVaultKey(pass, salt));
            _vault.Open(Vault.DefaultVaultPath, key);
            _user = _vault.LoadIdentity();

            if (_user == null) {
                _vault.Dispose();
                _vault = new Vault();
                SetAuthStatus("wrong password", true);
                BtnLogin.IsEnabled = true;
                return;
            }

            if (!string.Equals(_user.ServerUrl, serverUrl, StringComparison.Ordinal)) {
                _user.ServerUrl = serverUrl;
                _vault.SaveIdentity(_user);
            }

            if (LoginRememberSession.IsChecked == true) Session.Save(key);
            else Session.Clear();
            ReportVaultMaintenance();
            AppLog.Info("auth", $"manual login succeeded for {AppTelemetry.MaskUserId(_user.UserId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            await StartChatAsync();
        } catch (Exception ex) {
            AppLog.Error("auth", "vault unlock failed", ex);
            _vault.Dispose();
            _vault = new Vault();
            SetAuthStatus(FriendlyErrors.ToUserMessage(ex), true);
            BtnLogin.IsEnabled = true;
        }
    }

    void BtnRegister_Click(object s, RoutedEventArgs e) =>
        RunUiTask(RegisterAsync, "create vault", showSidebarErrors: false, onError: message => {
            SetAuthStatus(message, true);
            BtnRegister.IsEnabled = true;
        });

    async Task RegisterAsync() {
        var started = AppTelemetry.StartTimer();
        var name = RegName.Text.Trim();
        var pass = RegPass.Password;
        var pass2 = RegPass2.Password;
        var serverUrl = RelayUrl.Normalize(RegServerUrl.Text);

        if (string.IsNullOrEmpty(name)) { SetAuthStatus("enter a display name", true); return; }
        if (pass != pass2) { SetAuthStatus("passwords do not match", true); return; }
        if (!RelayUrl.IsValid(serverUrl)) { SetAuthStatus(RelayUrl.ValidationHint, true); return; }
        // No other password requirements — Argon2id handles security

        SetAuthStatus("generating keys & vault (takes ~3s)...", false);
        BtnRegister.IsEnabled = false;
        AppLog.Info("auth", $"vault creation started - display_name={name} relay={AppTelemetry.DescribeRelay(serverUrl)}");

        try {
            // Generate identity
            var (signPriv, signPub) = await Task.Run(Crypto.GenerateSigningKeys);
            var (dhPriv, dhPub) = await Task.Run(Crypto.GenerateDhKeys);
            var userId = Crypto.DeriveUserId(signPub);

            // Derive vault key
            var salt = Crypto.GenerateSalt();
            var key = await Task.Run(() => Crypto.DeriveVaultKey(pass, salt));

            // Save salt (unencrypted, needed to derive key on login)
            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(Vault.SaltPath)!);
            await System.IO.File.WriteAllBytesAsync(Vault.SaltPath, salt);

            _user = new LocalUser {
                UserId = userId,
                DisplayName = name,
                SignPrivKey = signPriv,
                SignPubKey = signPub,
                DhPrivKey = dhPriv,
                DhPubKey = dhPub,
                ServerUrl = serverUrl,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _vault.Open(Vault.DefaultVaultPath, key);
            _vault.SaveIdentity(_user);

            if (RegisterRememberSession.IsChecked == true) Session.Save(key);
            else Session.Clear();
            ReportVaultMaintenance();
            AppLog.Info("auth", $"vault creation succeeded for {AppTelemetry.MaskUserId(_user.UserId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            await StartChatAsync();
        } catch (Exception ex) {
            AppLog.Error("auth", "vault creation failed", ex);
            SetAuthStatus(FriendlyErrors.ToUserMessage(ex), true);
            BtnRegister.IsEnabled = true;
        }
    }

    void SetAuthStatus(string msg, bool isError) {
        AuthStatus.Text = msg;
        AuthStatus.Foreground = isError
            ? (Brush)FindResource("Red")
            : (Brush)FindResource("Dim");
        AuthStatus.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
    }

    void ApplyTheme(string themeFile, bool persist = true) {
        if (!UiThemeManager.TryApplyTheme(themeFile)) {
            ShowToast($"theme unavailable: {themeFile}", ToastSeverity.Warning);
            return;
        }

        _activeThemeFile = themeFile;
        if (persist) {
            try {
                _shellPreferences = AppShellPreferencesStore.Sanitize(_shellPreferences with {
                    ThemeFile = themeFile
                });
                AppShellPreferencesStore.Save(_shellPreferences);
            } catch (Exception ex) {
                AppLog.Warn("theme", $"failed to persist theme: {ex.Message}");
            }
        }

        UpdateThemeButtonStates();
        _settingsWindow?.ApplyThemeSelection(themeFile);
        RefreshDiagnosticsSummary();
    }

    void ApplySavedChatFontSize() {
        var fontSize = _shellPreferences.ChatFontSize;
        if (Math.Abs(fontSize - DefaultChatFontSize) < 0.01 && _vault.IsOpen) {
            var legacyRaw = _vault.GetSetting(UiChatFontSizeSettingKey);
            if (double.TryParse(legacyRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) {
                fontSize = parsed;
            }
        }

        ApplyChatFontSize(fontSize, persist: Math.Abs(_shellPreferences.ChatFontSize - fontSize) >= 0.01);
    }

    void ApplyChatFontSize(double fontSize, bool persist = true) {
        var normalized = NormalizeChatFontSize(fontSize);
        if (Application.Current?.Resources is ResourceDictionary resources) {
            resources["ChatMessageFontSize"] = normalized;
            resources["ChatSenderFontSize"] = Math.Max(12d, normalized - 4d);
            resources["ChatMetaFontSize"] = Math.Max(10.5d, normalized - 6d);
            resources["ConversationNameFontSize"] = Math.Max(13d, normalized - 3d);
            resources["ConversationPreviewFontSize"] = Math.Max(11.5d, normalized - 5d);
            resources["ComposerFontSize"] = normalized;
            resources["ComposerPlaceholderFontSize"] = Math.Max(14d, normalized - 1d);
        }

        if (persist) {
            try {
                _shellPreferences = AppShellPreferencesStore.Sanitize(_shellPreferences with {
                    ChatFontSize = normalized
                });
                AppShellPreferencesStore.Save(_shellPreferences);
            } catch (Exception ex) {
                AppLog.Warn("theme", $"failed to persist chat font size: {ex.Message}");
            }
        }

        _settingsWindow?.ApplyChatFontSizeSelection(normalized);
        RefreshDiagnosticsSummary();
        UpdateInputPlaceholderState();
    }

    double GetCurrentChatFontSize() =>
        Application.Current?.TryFindResource("ChatMessageFontSize") is double fontSize
            ? NormalizeChatFontSize(fontSize)
            : DefaultChatFontSize;

    static double NormalizeChatFontSize(double fontSize) {
        if (fontSize <= 15.5) return 15d;
        if (fontSize <= 18d) return 17d;
        return 19d;
    }

    void ApplySavedTheme() {
        var savedTheme = _shellPreferences.ThemeFile;
        if (_vault.IsOpen &&
            (string.IsNullOrWhiteSpace(savedTheme) ||
             string.Equals(savedTheme, DefaultThemeFile, StringComparison.Ordinal))) {
            var legacyTheme = _vault.GetSetting(UiThemeSettingKey);
            if (!string.IsNullOrWhiteSpace(legacyTheme)) {
                savedTheme = legacyTheme;
            }
        }

        ApplyTheme(savedTheme, persist: !string.Equals(savedTheme, _shellPreferences.ThemeFile, StringComparison.Ordinal));
    }

    void UpdateThemeButtonStates() {
        foreach (var button in new[] { BtnThemeTeal, BtnThemeViolet, BtnThemeBlue, BtnThemeAmber, BtnThemeRose }) {
            var isActive = string.Equals(button.Tag as string, _activeThemeFile, StringComparison.Ordinal);
            button.Content = isActive ? "✓" : "";
            button.BorderThickness = isActive ? new Thickness(2) : new Thickness(1);
            button.Foreground = isActive ? (Brush)FindResource("White") : (Brush)FindResource("Transparent");
        }
        _settingsWindow?.ApplyThemeSelection(_activeThemeFile);
    }

    void UpdateConversationSnapshot(ConvViewModel conv, Message? latestMessage = null, string? senderLabel = null) {
        latestMessage ??= _vault.LoadMessages(conv.Id, 0, 1).LastOrDefault();
        if (latestMessage == null) {
            conv.LastMessagePreview = "No messages yet";
            conv.LastMessageTimestamp = 0;
            return;
        }

        conv.LastMessagePreview = BuildConversationPreview(latestMessage, senderLabel);
        conv.LastMessageTimestamp = latestMessage.Timestamp;
    }

    string BuildConversationPreview(Message message, string? senderLabel) {
        var collapsed = string.Join(" ", message.Content
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(collapsed))
            collapsed = "(empty message)";

        var preview = message.ConvType == ConversationType.Group
            ? $"{(message.IsMine ? "You" : senderLabel ?? ResolveUserLabel(message.SenderId))}: {collapsed}"
            : collapsed;

        return preview.Length <= 44 ? preview : preview[..41] + "...";
    }

    // ── Start chat ────────────────────────────────────────────────────────

    async Task StartChatAsync() {
        _user!.ServerUrl = RelayUrl.Normalize(_user.ServerUrl);
        AppLog.Info("chat", $"starting session for {AppTelemetry.MaskUserId(_user!.UserId)} via {AppTelemetry.DescribeRelay(_user.ServerUrl)}");
        AuthPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        ApplySavedTheme();
        ApplySavedChatFontSize();
        SetConversationSurfaceState(false);

        // Show user ID in header (truncated)
        BtnMyId.Content = $"ID {ShortUserId(_user!.UserId)}";
        UpdateAutomationIdentity();

        // Load contacts and groups
        LoadConversations();

        // Connect to relay
        WireNetworkEvents();
        ApplyConnectionState(RelayConnectionState.Connecting, "connecting to relay...");
        SignalChatReady();
        await _net.ConnectAsync(_user);
    }

    void LoadConversations() {
        _convs.Clear();
        var contacts = _vault.LoadContacts();
        foreach (var c in contacts) {
            var conv = new ConvViewModel {
                Id = c.ConversationId ?? c.UserId,
                DisplayName = c.DisplayName,
                IsGroup = false,
                ContactData = c,
                IsMuted = IsConversationMuted(c.ConversationId ?? c.UserId)
            };
            UpdateConversationSnapshot(conv);
            _convs.Add(conv);
        }
        var groups = _vault.LoadGroups();
        foreach (var g in groups) {
            var conv = new ConvViewModel {
                Id = g.GroupId,
                DisplayName = g.Name,
                IsGroup = true,
                GroupData = g,
                IsMuted = IsConversationMuted(g.GroupId)
            };
            UpdateConversationSnapshot(conv);
            _convs.Add(conv);
        }
        AppLog.Info("chat", $"loaded conversations - direct={contacts.Count} groups={groups.Count}");
    }

    // ── Network event wiring ─────────────────────────────────────────────

    void WireNetworkEvents() {
        if (_networkEventsWired) return;
        _networkEventsWired = true;

        _net.OnStateChanged += (state, detail) => Dispatcher.InvokeAsync(() =>
            ApplyConnectionState(state, detail));

        _net.OnConnected += () => Dispatcher.InvokeAsync(() => {
            AppLog.Info("relay", "connected");
            ApplyConnectionState(RelayConnectionState.Connected, "relay connected");
            // Announce presence to all contacts
            var ids = _convs.Where(c => !c.IsGroup).Select(c => c.ContactData!.UserId).ToList();
            _ = _net.AnnouncePresenceAsync(ids);
            foreach (var contact in _convs.Where(c => !c.IsGroup)
                                          .Select(c => c.ContactData!)
                                          .Where(c => c.SignPubKey.Length == 0 || c.DhPubKey.Length == 0))
                RunUiTask(async () => { await EnsureContactKeysAsync(contact); }, "refresh contact keys", showSidebarErrors: false);
            RunUiTask(FlushOutboxAsync, "flush outbox", showSidebarErrors: false);
        });

        _net.OnDisconnected += () => Dispatcher.InvokeAsync(() => {
            AppLog.Warn("relay", "disconnected");
            ApplyConnectionState(RelayConnectionState.Reconnecting, "relay connection lost - retrying...");
            foreach (var c in _convs.Where(c => !c.IsGroup))
                c.IsOnline = false;
        });

        _net.OnUserOnline += uid => Dispatcher.InvokeAsync(() => {
            var conv = _convs.FirstOrDefault(c => c.ContactData?.UserId == uid);
            if (conv?.ContactData is { } contact) {
                conv.IsOnline = true;
                RunUiTask(async () => { await EnsureContactKeysAsync(contact); }, "refresh contact keys", showSidebarErrors: false);
            }
        });

        _net.OnMessage += (senderId, payload, sig, seq, ts) =>
            RunUiTask(() => HandleIncomingDmAsync(senderId, payload, sig, seq, ts),
                "handle direct message");

        _net.OnGroupMessage += (groupId, senderId, payload, sig, seq, ts) =>
            RunUiTask(() => HandleIncomingGroupAsync(groupId, senderId, payload, sig, seq, ts),
                "handle group message");

        _net.OnError += msg => Dispatcher.InvokeAsync(() => {
            AppLog.Warn("relay", msg);
            SetSidebarStatus($"! {msg}", transientRelay: true);
        });
    }

    void ApplyConnectionState(RelayConnectionState state, string? detail) {
        SignalRelayState(state, detail);
        ConnDot.Text = "●";
        switch (state) {
            case RelayConnectionState.Connected:
                ConnDot.Foreground = (Brush)FindResource("Green");
                ConnStatusText.Text = "online";
                ConnStatusText.Foreground = (Brush)FindResource("Green");
                ClearTransientRelayStatus();
                if (!string.IsNullOrWhiteSpace(detail))
                    SetSidebarStatus(detail, transientRelay: true);
                break;
            case RelayConnectionState.Connecting:
                ConnDot.Foreground = (Brush)FindResource("Amber");
                ConnStatusText.Text = "connecting";
                ConnStatusText.Foreground = (Brush)FindResource("Amber");
                if (!string.IsNullOrWhiteSpace(detail))
                    SetSidebarStatus(detail, transientRelay: true);
                break;
            case RelayConnectionState.Reconnecting:
                ConnDot.Foreground = (Brush)FindResource("Amber");
                ConnStatusText.Text = "retrying";
                ConnStatusText.Foreground = (Brush)FindResource("Amber");
                if (!string.IsNullOrWhiteSpace(detail))
                    SetSidebarStatus(detail, transientRelay: true);
                break;
            default:
                ConnDot.Foreground = (Brush)FindResource("Red");
                ConnStatusText.Text = "offline";
                ConnStatusText.Foreground = (Brush)FindResource("Red");
                if (!string.IsNullOrWhiteSpace(detail))
                    SetSidebarStatus(detail, transientRelay: true);
                break;
        }
    }

    void ClearTransientRelayStatus() {
        for (var i = _toasts.Count - 1; i >= 0; i--) {
            if (string.Equals(_toasts[i].Category, "relay-status", StringComparison.Ordinal)) {
                _toasts.RemoveAt(i);
            }
        }
    }

    void SetSidebarStatus(string message, bool transientRelay = false) {
        if (string.IsNullOrWhiteSpace(message)) return;
        ShowToast(message, ClassifyToastSeverity(message), transientRelay ? "relay-status" : "");
    }

    static ToastSeverity ClassifyToastSeverity(string message) {
        if (message.StartsWith("!", StringComparison.Ordinal))
            return ToastSeverity.Error;

        if (message.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("review", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("queued", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("can't", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            return ToastSeverity.Warning;

        return ToastSeverity.Info;
    }

    static Brush WithOpacity(Brush source, double opacity) {
        var clone = source.CloneCurrentValue();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }

    void ShowToast(string message, ToastSeverity severity, string category = "") {
        if (_toasts.Any(t => string.Equals(t.Message, message, StringComparison.Ordinal)))
            return;

        if (!string.IsNullOrWhiteSpace(category)) {
            for (var i = _toasts.Count - 1; i >= 0; i--) {
                if (string.Equals(_toasts[i].Category, category, StringComparison.Ordinal)) {
                    _toasts.RemoveAt(i);
                }
            }
        }

        var accent = severity switch {
            ToastSeverity.Error => B.Red,
            ToastSeverity.Warning => B.Amber,
            _ => B.Green
        };
        var toast = new ToastViewModel {
            Message = message.TrimStart('!', ' '),
            DotBrush = accent,
            BorderBrush = WithOpacity(accent, 0.4),
            BackgroundBrush = WithOpacity(accent, 0.18),
            ForegroundBrush = severity switch {
                ToastSeverity.Error => B.AmberText,
                ToastSeverity.Warning => B.AmberText,
                _ => B.TextGreen
            },
            CanDismiss = severity != ToastSeverity.Info,
            Category = category
        };

        _toasts.Insert(0, toast);
        if (_toasts.Count > 4)
            _toasts.RemoveAt(_toasts.Count - 1);

        if (!toast.CanDismiss)
            _ = AutoDismissToastAsync(toast, TimeSpan.FromSeconds(3));
    }

    async Task AutoDismissToastAsync(ToastViewModel toast, TimeSpan delay) {
        try {
            await Task.Delay(delay, _uiLifetimeCts.Token);
            await Dispatcher.InvokeAsync(() => _toasts.Remove(toast));
        } catch (TaskCanceledException) {
        }
    }

    void DismissToast_Click(object s, RoutedEventArgs e) {
        if ((s as FrameworkElement)?.DataContext is ToastViewModel toast)
            _toasts.Remove(toast);
    }

    void CopyMessageMenuItem_Click(object s, RoutedEventArgs e) {
        if ((s as FrameworkElement)?.DataContext is not MessageViewModel vm ||
            string.IsNullOrWhiteSpace(vm.Content))
            return;

        Clipboard.SetText(vm.Content);
        ShowToast("Message copied", ToastSeverity.Info);
    }

    void MainWindow_PreviewKeyDown(object s, KeyEventArgs e) {
        if (Keyboard.Modifiers != ModifierKeys.Control || ChatPanel.Visibility != Visibility.Visible)
            return;

        if (e.Key == Key.N) {
            BtnAddContact_Click(BtnSidebarAddFriend, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.G) {
            BtnQuickNewGroup_Click(BtnQuickNewGroup, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    void InitializeDesktopNotifications() {
        try {
            var processPath = Environment.ProcessPath;
            System.Drawing.Icon? icon = null;
            if (!string.IsNullOrWhiteSpace(processPath) && System.IO.File.Exists(processPath)) {
                icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            }

            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(BringWindowToFront));
            trayMenu.Items.Add("Open settings", null, (_, _) => Dispatcher.Invoke(() => {
                BringWindowToFront();
                InitializeShellPreferences();
                RefreshDiagnosticsSummary();
                UpdateThemeButtonStates();
                ApplyShellPreferencesToUi();
                OpenSettingsWindow();
            }));
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

            _notifyIcon = new Forms.NotifyIcon {
                Text = AppBranding.WindowTitle,
                Visible = true,
                BalloonTipIcon = Forms.ToolTipIcon.Info,
                Icon = icon ?? System.Drawing.SystemIcons.Information,
                ContextMenuStrip = trayMenu
            };
            _notifyIcon.BalloonTipClicked += (_, _) => BringWindowToFront();
            _notifyIcon.DoubleClick += (_, _) => BringWindowToFront();
        } catch (Exception ex) {
            AppLog.Warn("notify", $"desktop notifications unavailable: {ex.Message}");
        }
    }

    bool HideToTray(bool showHint) {
        if (_notifyIcon == null) return false;

        GroupMenuPopup.IsOpen = false;
        AddOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        NukeOverlay.Visibility = Visibility.Collapsed;
        _settingsWindow?.Close();
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
        Hide();

        if (showHint && !_trayCloseHintShown) {
            try {
                _notifyIcon.BalloonTipTitle = AppBranding.WindowTitle;
                _notifyIcon.BalloonTipText = "Still running in the tray. Double-click to reopen or right-click to exit.";
                _notifyIcon.ShowBalloonTip(4500);
            } catch {
            }

            _trayCloseHintShown = true;
        }

        return true;
    }

    void ExitFromTray() {
        _exitRequested = true;
        Close();
    }

    bool ShouldNotifyForConversation(string conversationId) {
        var conv = _convs.FirstOrDefault(c => string.Equals(c.Id, conversationId, StringComparison.Ordinal));
        if (conv?.IsMuted == true) return false;

        return WindowState == WindowState.Minimized ||
            !IsActive ||
            !string.Equals(_activeConvId, conversationId, StringComparison.Ordinal);
    }

    void ShowDesktopNotification(string title, string body) {
        if (_notifyIcon == null || string.IsNullOrWhiteSpace(body)) return;

        try {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = body.Length > 220 ? body[..217] + "..." : body;
            _notifyIcon.ShowBalloonTip(4500);
            AppLog.Info("notify", $"desktop notification shown title={title}");
        } catch (Exception ex) {
            AppLog.Warn("notify", $"failed to show notification: {ex.Message}");
        }
    }

    static string ShortUserId(string? userId) {
        if (string.IsNullOrWhiteSpace(userId)) return "";
        var trimmed = userId.Trim();
        return trimmed.Length <= 8 ? trimmed : trimmed[..8];
    }

    static bool IsValidUserId(string userId) {
        if (userId.Length != 22) return false;

        foreach (var ch in userId) {
            var isAsciiLetter = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
            var isDigit = ch >= '0' && ch <= '9';
            if (!isAsciiLetter && !isDigit && ch != '-' && ch != '_') {
                return false;
            }
        }

        return true;
    }

    void BringWindowToFront() {
        Dispatcher.Invoke(() => {
            if (!IsVisible) {
                ShowInTaskbar = true;
                Show();
            }
            if (WindowState == WindowState.Minimized) {
                WindowState = WindowState.Normal;
            }
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    void RunUiTask(Func<Task> work, string operation,
        bool showSidebarErrors = true, Action<string>? onError = null) {
        _ = Dispatcher.InvokeAsync(async () => {
            try {
                await work();
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                AppLog.Error("ui", $"{operation} failed", ex);
                var message = FriendlyErrors.ToUserMessage(ex);
                if (onError != null) onError(message);
                else if (showSidebarErrors) SetSidebarStatus($"! {message}");
            }
        }).Task;
    }

    // ── Incoming message handlers ─────────────────────────────────────────

    async Task HandleIncomingDmAsync(string senderId, string payload, string sig, long seq, long ts) {
        // Find contact
        var conv = _convs.FirstOrDefault(c => c.ContactData?.UserId == senderId);
        if (conv == null) return; // Unknown sender — ignore

        var contact = conv.ContactData!;
        AppLog.Info("recv", $"direct envelope from {AppTelemetry.MaskUserId(senderId)} seq={seq} bytes={payload.Length}");
        if (!await EnsureContactKeysAsync(contact)) return;

        // Verify signature (server already checked, client double-checks)
        if (!Crypto.Verify(contact.SignPubKey, $"{payload}:{seq}", sig)) return;

        // Replay protection
        if (!IsNewSeq(conv.Id, contact.UserId, seq)) {
            await _net.AckDmAsync(senderId, seq);
            return;
        }

        // Get/derive shared secret
        var convKey = GetOrDeriveConvKey(contact);
        if (convKey == null) return;

        var msg = Crypto.DecryptDm(convKey, senderId, payload);
        if (msg == null) return;

        msg.ConversationId = conv.Id;
        msg.IsMine = false;
        if (TryHandleDirectSystemMessage(contact, msg)) {
            await _net.AckDmAsync(senderId, seq);
            AppLog.Info("recv", $"processed system direct message from {AppTelemetry.MaskUserId(senderId)} seq={seq}");
            return;
        }

        if (_vault.MessageExists(msg.Id)) {
            await _net.AckDmAsync(senderId, seq);
            AppLog.Warn("recv", $"ignored duplicate direct message {AppTelemetry.MaskUserId(msg.Id)} seq={seq}");
            return;
        }

        _vault.SaveMessage(msg);
        UpdateConversationSnapshot(conv, msg, contact.DisplayName);
        await _net.AckDmAsync(senderId, seq);
        await SendDirectReceiptAsync(contact, msg, ReceiptStatusReceived);

        // If this conversation is active, show the message
        if (conv.Id == _activeConvId) {
            var vm = ToViewModel(msg, contact.DisplayName);
            AddMessageToList(vm);
            await SendDirectReceiptAsync(contact, msg, ReceiptStatusSeen);
        } else {
            conv.UnreadCount++;
        }
        if (ShouldNotifyForConversation(conv.Id)) {
            ShowDesktopNotification(contact.DisplayName, $"New message from {contact.DisplayName}");
        }
        AppLog.Info("recv", $"stored direct message {AppTelemetry.MaskUserId(msg.Id)} seq={seq} active={(conv.Id == _activeConvId)}");

        await Task.CompletedTask;
    }

    async Task HandleIncomingGroupAsync(string groupId, string senderId, string payload, string sig, long seq, long ts) {
        // Handle group prefix in payload from server
        var actualPayload = payload.StartsWith($"GROUP:{groupId}:")
            ? payload[$"GROUP:{groupId}:".Length..] : payload;

        var conv = _convs.FirstOrDefault(c => c.GroupData?.GroupId == groupId);
        if (conv == null) return;

        var group = conv.GroupData!;
        AppLog.Info("recv", $"group envelope group={AppTelemetry.MaskUserId(groupId)} from {AppTelemetry.MaskUserId(senderId)} seq={seq} bytes={payload.Length}");

        // Find sender contact for signature verification
        var senderContact = _convs.FirstOrDefault(c =>
            string.Equals(c.ContactData?.UserId, senderId, StringComparison.Ordinal))?.ContactData;
        if (senderContact == null) return;
        if (!await EnsureContactKeysAsync(senderContact)) return;
        if (!Crypto.Verify(senderContact.SignPubKey, $"{actualPayload}:{seq}", sig)) return;
        if (!IsNewSeq(groupId, senderId, seq)) {
            await _net.AckGroupAsync(groupId, senderId, seq);
            return;
        }

        var msg = Crypto.DecryptGroup(group.GroupKey, groupId, senderId, actualPayload);
        if (msg == null) return;

        msg.ConversationId = groupId;
        msg.IsMine = false;
        if (_vault.MessageExists(msg.Id)) {
            await _net.AckGroupAsync(groupId, senderId, seq);
            AppLog.Warn("recv", $"ignored duplicate group message {AppTelemetry.MaskUserId(msg.Id)} seq={seq}");
            return;
        }
        _vault.SaveMessage(msg);
        UpdateConversationSnapshot(conv, msg, senderContact.DisplayName);
        await _net.AckGroupAsync(groupId, senderId, seq);
        await SendGroupReceiptAsync(senderContact, msg, ReceiptStatusReceived);

        if (groupId == _activeConvId) {
            var senderName = senderContact.DisplayName;
            AddMessageToList(ToViewModel(msg, senderName));
            await SendGroupReceiptAsync(senderContact, msg, ReceiptStatusSeen);
        } else {
            conv.UnreadCount++;
        }
        if (ShouldNotifyForConversation(groupId)) {
            ShowDesktopNotification(group.Name, $"New message from {senderContact.DisplayName}");
        }
        AppLog.Info("recv", $"stored group message {AppTelemetry.MaskUserId(msg.Id)} seq={seq} active={(groupId == _activeConvId)}");

        await Task.CompletedTask;
    }

    // ── Conversation selection ─────────────────────────────────────────────

    void ConvList_SelectionChanged(object s, SelectionChangedEventArgs e) {
        if (ConvList.SelectedItem is not ConvViewModel conv) return;
        OpenConversation(conv);
    }

    void OpenConversation(ConvViewModel conv) {
        var started = AppTelemetry.StartTimer();
        _activeConv = conv;
        _activeConvId = conv.Id;
        var unreadCount = conv.UnreadCount;
        conv.UnreadCount = 0;

        ActiveConvName.Text = conv.IsGroup
            ? $"Group · {conv.DisplayName}"
            : $"Direct · {conv.DisplayName}" +
              (string.IsNullOrWhiteSpace(conv.ContactData?.UserId)
                  ? ""
                  : $" · {ShortUserId(conv.ContactData?.UserId)}");

        SetConversationSurfaceState(true);
        UpdateActiveConversationSecurityUi();
        if (InputBox.IsEnabled) InputBox.Focus();

        // Load initial messages
        LoadInitialMessages(conv.Id, unreadCount);
        RunUiTask(MarkActiveConversationSeenAsync, "mark active messages seen", showSidebarErrors: false);
        AppLog.Info("ui", $"opened {AppTelemetry.DescribeConversation(conv.Id, conv.IsGroup)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
    }

    void UpdateActiveConversationSecurityUi() {
        var groupInviteVisible = _activeConv?.IsGroup == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnGroupInvite.Visibility = Visibility.Collapsed;
        BtnGroupMenu.Visibility = groupInviteVisible;
        RefreshMuteButton();
        if (_activeConv?.IsGroup == true) {
            RefreshActiveGroupMenu();
        } else {
            GroupMenuPopup.IsOpen = false;
        }

        if (_activeConv == null) {
            BtnSecurityReview.Visibility = Visibility.Collapsed;
            InputBox.IsEnabled = false;
            UpdateComposerState();
            return;
        }

        if (_activeConv?.ContactData is not Contact contact) {
            BtnSecurityReview.Visibility = Visibility.Collapsed;
            InputBox.IsEnabled = true;
            UpdateComposerState();
            return;
        }

        BtnSecurityReview.Visibility = Visibility.Visible;
        if (contact.HasPendingKeyChange) {
            BtnSecurityReview.Content = "Review";
            BtnSecurityReview.BorderBrush = (Brush)FindResource("Red");
            BtnSecurityReview.Foreground = (Brush)FindResource("Red");
            BtnSecurityReview.ToolTip = "contact keys changed - review before sending";
            InputBox.IsEnabled = false;
            SetSidebarStatus($"security review required: {contact.DisplayName}'s relay keys changed");
            BtnSecurityReview.Content = "Review Keys";
            UpdateComposerState();
            return;
        }

        BtnSecurityReview.BorderBrush = contact.IsVerified
            ? (Brush)FindResource("Green")
            : (Brush)FindResource("Amber");
        BtnSecurityReview.Foreground = contact.IsVerified
            ? (Brush)FindResource("Green")
            : (Brush)FindResource("Amber");
        BtnSecurityReview.Content = contact.IsVerified ? "Verified" : "Verify";
        BtnSecurityReview.ToolTip = contact.IsVerified
            ? "contact safety number verified"
            : "compare and verify this contact's safety number";
        InputBox.IsEnabled = true;
        UpdateComposerState();
    }

    // ── Lazy message loading ───────────────────────────────────────────────

    /// <summary>
    /// Load the initial page of messages (newest 50).
    /// This is the only method that replaces the entire list.
    /// All other loads are incremental (prepend older).
    /// </summary>
    void LoadInitialMessages(string convId, int unreadCount = 0) {
        var started = AppTelemetry.StartTimer();
        _messages.Clear();
        _msgOffset = 0;
        _msgTotal = _vault.GetMessageCount(convId);

        var page = _vault.LoadMessages(convId, 0, 50);
        _msgOffset = page.Count;
        HydrateReceiptCache(page);

        var contacts = _vault.LoadContacts().ToDictionary(c => c.UserId, c => c.DisplayName);
        var separatorIndex = unreadCount > 0
            ? Math.Max(0, page.Count - Math.Min(unreadCount, page.Count))
            : -1;

        for (int i = 0; i < page.Count; i++) {
            if (i == separatorIndex)
                _messages.Add(new UnreadSeparatorViewModel());

            var msg = page[i];
            _messages.Add(ToViewModel(msg, contacts.GetValueOrDefault(msg.SenderId, ShortUserId(msg.SenderId))));
        }

        // Scroll to bottom after layout — use Loaded priority to ensure items are rendered
        Dispatcher.InvokeAsync(ScrollToBottom, DispatcherPriority.Loaded);
        AppLog.Info("history", $"loaded initial page for {AppTelemetry.DescribeConversation(convId, _activeConv?.IsGroup == true)} count={page.Count} total={_msgTotal} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
    }

    /// <summary>
    /// Scroll changed handler — triggers history load when user scrolls to top.
    /// Carefully preserves scroll position so the view doesn't jump.
    /// </summary>
    void OnScrollChanged(object s, ScrollChangedEventArgs e) =>
        RunUiTask(() => HandleScrollChangedAsync(e), "load older messages", showSidebarErrors: false);

    async Task HandleScrollChangedAsync(ScrollChangedEventArgs e) {
        // Track whether user is near bottom (for auto-scroll on new messages)
        _isNearBottom = _msgScrollViewer != null &&
            _msgScrollViewer.VerticalOffset >= _msgScrollViewer.ScrollableHeight - 80;

        // Trigger history load when near top and there are more messages
        if (e.VerticalOffset < 40 && e.VerticalChange < 0 && !_loadingHistory && _msgOffset < _msgTotal)
            await LoadOlderMessagesAsync();
    }

    async Task LoadOlderMessagesAsync() {
        if (_loadingHistory || _msgOffset >= _msgTotal) return;
        var started = AppTelemetry.StartTimer();
        _loadingHistory = true;
        LoadMoreBar.Visibility = Visibility.Visible;

        var convId = _activeConvId;
        var contacts = _vault.LoadContacts().ToDictionary(c => c.UserId, c => c.DisplayName);

        // Load on background thread
        var older = await Task.Run(() => _vault.LoadMessages(convId, _msgOffset, 50));
        _msgOffset += older.Count;
        HydrateReceiptCache(older);

        if (older.Count == 0) {
            _loadingHistory = false;
            LoadMoreBar.Visibility = Visibility.Collapsed;
            return;
        }

        // Record scroll position BEFORE adding items
        var prevExtentHeight = _msgScrollViewer?.ExtentHeight ?? 0;

        // Prepend to collection (older messages go at top)
        for (int i = older.Count - 1; i >= 0; i--) {
            var msg = older[i];
            _messages.Insert(0, ToViewModel(msg,
                contacts.GetValueOrDefault(msg.SenderId, ShortUserId(msg.SenderId))));
        }

        // Restore scroll position AFTER items render
        await Dispatcher.InvokeAsync(() => {
            if (_msgScrollViewer != null) {
                var newOffset = _msgScrollViewer.ExtentHeight - prevExtentHeight;
                _msgScrollViewer.ScrollToVerticalOffset(newOffset);
            }
            LoadMoreBar.Visibility = Visibility.Collapsed;
        }, DispatcherPriority.Loaded);

        _loadingHistory = false;
        AppLog.Info("history", $"loaded older page for {AppTelemetry.DescribeConversation(convId, _activeConv?.IsGroup == true)} added={older.Count} offset={_msgOffset}/{_msgTotal} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
    }

    /// <summary>
    /// Add a new incoming/sent message to the list.
    /// Only auto-scrolls if user was already near bottom.
    /// </summary>
    void AddMessageToList(MessageViewModel vm) {
        var shouldStickToBottom = IsMessageListNearBottom();
        ApplyReceiptUi(vm);
        _messages.Add(vm);
        _msgOffset++;
        _msgTotal++;

        _isNearBottom = shouldStickToBottom;
        if (shouldStickToBottom) {
            Dispatcher.InvokeAsync(() => {
                MessageList.ScrollIntoView(vm);
                ScrollToBottom();
            }, DispatcherPriority.Background);
            Dispatcher.InvokeAsync(ScrollToBottom, DispatcherPriority.Loaded);
        }
    }

    void ScrollToBottom() {
        _msgScrollViewer?.ScrollToEnd();
        _isNearBottom = true;
    }

    bool IsMessageListNearBottom(double threshold = 84d) =>
        _msgScrollViewer == null ||
        _msgScrollViewer.VerticalOffset >= _msgScrollViewer.ScrollableHeight - threshold;

    // ── Sending messages ──────────────────────────────────────────────────

    void InputBox_KeyDown(object s, KeyEventArgs e) {
        if ((e.Key is Key.Enter or Key.Return) && !Keyboard.IsKeyDown(Key.LeftShift) &&
            !Keyboard.IsKeyDown(Key.RightShift)) {
            e.Handled = true;
            RunUiTask(SendMessageAsync, "send message");
        }
    }

    void InputBox_TextChanged(object s, TextChangedEventArgs e) {
        UpdateComposerState();
        HideTypingIndicator();
    }

    void BtnSend_Click(object s, RoutedEventArgs e) =>
        RunUiTask(SendMessageAsync, "send message");

    async Task SendMessageAsync() {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _activeConv == null || _user == null) return;
        AppLog.Info("send", $"queueing {_activeConv.IsGroup switch { true => "group", false => "direct" }} message for {AppTelemetry.DescribeConversation(_activeConvId, _activeConv.IsGroup)} length={text.Length}");
        InputBox.Clear();

        var msg = new Message {
            ConversationId = _activeConvId,
            SenderId = _user.UserId,
            Content = text,
            IsMine = true,
            Status = MessageStatus.Sending,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        msg.ConvType = _activeConv.IsGroup ? ConversationType.Group : ConversationType.Direct;

        // Show optimistically
        var senderName = _user.DisplayName;
        var vm = ToViewModel(msg, senderName);
        UpdateConversationSnapshot(_activeConv, msg, senderName);
        AddMessageToList(vm);

        if (_activeConv.IsGroup) {
            await SendGroupMessageAsync(msg, vm);
        } else {
            await SendDmAsync(msg, vm);
        }
    }

    async Task SendDmAsync(Message msg, MessageViewModel vm) {
        var started = AppTelemetry.StartTimer();
        var contact = _activeConv!.ContactData!;
        if (!await EnsureContactKeysAsync(contact)) {
            vm.Status = MessageStatus.Failed;
            ApplyReceiptUi(vm);
            SetSidebarStatus(contact.HasPendingKeyChange
                ? $"can't send until you review {contact.DisplayName}'s new safety number"
                : $"can't send yet: {contact.DisplayName} hasn't registered keys with this relay");
            AppLog.Warn("send", $"direct send blocked for {AppTelemetry.MaskUserId(contact.UserId)} pending_keys={contact.HasPendingKeyChange}");
            return;
        }
        var convKey = GetOrDeriveConvKey(contact);
        if (convKey == null) {
            vm.Status = MessageStatus.Failed;
            ApplyReceiptUi(vm);
            AppLog.Warn("send", $"direct send failed to derive conversation key for {AppTelemetry.MaskUserId(contact.UserId)}");
            return;
        }

        msg.SeqNum = _vault.NextSeqNum(msg.ConversationId);
        var payload = Crypto.EncryptDm(convKey, msg);
        var sig = Crypto.SignPayload(_user!.SignPrivKey, payload, msg.SeqNum);

        _vault.SaveMessage(msg);

        var sent = await _net.SendDmAsync(contact.UserId, payload, sig, msg.SeqNum);
        if (sent) {
            vm.Status = MessageStatus.Sent;
            _vault.UpdateMessageStatus(msg.Id, MessageStatus.Sent);
            ApplyReceiptUi(vm);
            AppLog.Info("send", $"direct message sent to {AppTelemetry.MaskUserId(contact.UserId)} seq={msg.SeqNum} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
        } else {
            // Queue in outbox for retry
            vm.Status = MessageStatus.Sending;
            ApplyReceiptUi(vm);
            _vault.EnqueueOutbox(msg.Id, contact.UserId, payload, sig, msg.SeqNum);
            AppLog.Warn("send", $"direct message queued for retry to {AppTelemetry.MaskUserId(contact.UserId)} seq={msg.SeqNum}");
        }
    }

    async Task SendGroupMessageAsync(Message msg, MessageViewModel vm) {
        var started = AppTelemetry.StartTimer();
        var group = _activeConv!.GroupData!;
        msg.SeqNum = _vault.NextSeqNum(msg.ConversationId);
        msg.ConvType = ConversationType.Group;
        var payload = Crypto.EncryptGroup(group.GroupKey, msg);
        var sig = Crypto.SignPayload(_user!.SignPrivKey, payload, msg.SeqNum);

        _vault.SaveMessage(msg);

        var sent = await _net.SendGroupAsync(group.GroupId, group.MemberIds, payload, sig, msg.SeqNum);
        vm.Status = sent ? MessageStatus.Sent : MessageStatus.Sending;
        ApplyReceiptUi(vm);
        if (!sent)
            _vault.EnqueueOutbox(msg.Id, group.GroupId, payload, sig, msg.SeqNum,
                ConversationType.Group, group.GroupId, group.MemberIds);
        else
            _vault.UpdateMessageStatus(msg.Id, MessageStatus.Sent);
        AppLog.Info("send", sent
            ? $"group message sent to {group.MemberIds.Count - 1} recipients seq={msg.SeqNum} in {AppTelemetry.ElapsedMilliseconds(started)}ms"
            : $"group message queued for retry group={AppTelemetry.MaskUserId(group.GroupId)} seq={msg.SeqNum}");
    }

    // ── Outbox flush (resend failed/offline messages) ──────────────────────

    async Task FlushOutboxAsync() {
        if (!_net.IsConnected || _flushingOutbox) return;

        _flushingOutbox = true;
        try {
            var pending = _vault.LoadOutbox();
            if (pending.Count > 0) {
                AppLog.Info("outbox", $"flush started with {pending.Count} pending item(s)");
            }
            foreach (var item in pending) {
                bool sent;
                if (item.ConvType == ConversationType.Group && item.MemberIds != null)
                    sent = await _net.SendGroupAsync(item.GroupId!, item.MemberIds, item.Payload, item.Sig, item.SeqNum);
                else
                    sent = await _net.SendDmAsync(item.RecipientId, item.Payload, item.Sig, item.SeqNum);

                if (sent) {
                    _vault.RemoveOutbox(item.Id);
                    _vault.UpdateMessageStatus(item.Id, MessageStatus.Sent);
                    var vm = MessageItems.FirstOrDefault(m => m.Id == item.Id);
                    if (vm != null) {
                        vm.Status = MessageStatus.Sent;
                        ApplyReceiptUi(vm);
                    }
                    AppLog.Info("outbox", $"delivered queued {item.ConvType} item seq={item.SeqNum} id={AppTelemetry.MaskUserId(item.Id)}");
                } else {
                    _vault.IncrementOutboxAttempts(item.Id);
                    AppLog.Warn("outbox", $"retry failed for queued {item.ConvType} item seq={item.SeqNum} id={AppTelemetry.MaskUserId(item.Id)}");
                }
            }
        } finally {
            _flushingOutbox = false;
        }
    }

    // ── Key management ────────────────────────────────────────────────────

    async Task<bool> EnsureContactKeysAsync(Contact contact) {
        if (contact.HasPendingKeyChange) return false;

        if (!_net.IsConnected) {
            return contact.SignPubKey.Length > 0 && contact.DhPubKey.Length > 0;
        }

        var started = AppTelemetry.StartTimer();
        var keys = await _net.GetUserKeysAsync(contact.UserId);
        if (keys == null) return contact.SignPubKey.Length > 0 && contact.DhPubKey.Length > 0;

        if (HasSameKeys(contact.SignPubKey, contact.DhPubKey, keys.Value.signPub, keys.Value.dhPub)) {
            if (contact.HasPendingKeyChange) {
                contact.PendingSignPubKey = [];
                contact.PendingDhPubKey = [];
                contact.KeyChangedAt = 0;
                _vault.SaveContact(contact);
            }

            UpdateActiveConversationSecurityUi();
            AppLog.Info("keys", $"contact key check unchanged for {AppTelemetry.MaskUserId(contact.UserId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return true;
        }

        if (contact.SignPubKey.Length == 0 || contact.DhPubKey.Length == 0) {
            contact.SignPubKey = keys.Value.signPub;
            contact.DhPubKey = keys.Value.dhPub;
            contact.PendingSignPubKey = [];
            contact.PendingDhPubKey = [];
            contact.KeyChangedAt = 0;
            _vault.SaveContact(contact);
            UpdateActiveConversationSecurityUi();
            AppLog.Info("keys", $"contact keys hydrated for {AppTelemetry.MaskUserId(contact.UserId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return true;
        }

        contact.PendingSignPubKey = keys.Value.signPub;
        contact.PendingDhPubKey = keys.Value.dhPub;
        contact.IsVerified = false;
        contact.KeyChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _vault.SaveContact(contact);
        CancelQueuedDirectMessages(contact);

        if (_activeConv?.ContactData?.UserId == contact.UserId) {
            UpdateActiveConversationSecurityUi();
        }

        SetSidebarStatus($"warning: {contact.DisplayName}'s relay keys changed. compare the new safety number before sending.");
        AppLog.Warn("keys", $"relay key change detected for {AppTelemetry.MaskUserId(contact.UserId)}");
        return false;
    }

    static bool HasSameKeys(byte[] currentSign, byte[] currentDh, byte[] nextSign, byte[] nextDh) =>
        currentSign.AsSpan().SequenceEqual(nextSign) &&
        currentDh.AsSpan().SequenceEqual(nextDh);

    void CancelQueuedDirectMessages(Contact contact) {
        var pending = _vault.LoadOutbox()
            .Where(item => item.ConvType == ConversationType.Direct && item.RecipientId == contact.UserId)
            .ToList();

        foreach (var item in pending) {
            _vault.RemoveOutbox(item.Id);
            _vault.UpdateMessageStatus(item.Id, MessageStatus.Failed);
            var vm = MessageItems.FirstOrDefault(m => m.Id == item.Id);
            if (vm != null) {
                vm.Status = MessageStatus.Failed;
                ApplyReceiptUi(vm);
            }
        }
    }

    string ComputeSafetyNumber(Contact contact, bool usePendingKeys = false) {
        var signPub = usePendingKeys ? contact.PendingSignPubKey : contact.SignPubKey;
        var dhPub = usePendingKeys ? contact.PendingDhPubKey : contact.DhPubKey;
        return Crypto.ComputeSafetyNumber(
            _user!.UserId, _user.SignPubKey, _user.DhPubKey,
            contact.UserId, signPub, dhPub);
    }

    void AcceptPendingContactKeys(Contact contact) {
        if (!contact.HasPendingKeyChange) return;

        var convId = contact.ConversationId ?? contact.UserId;
        if (_convKeys.Remove(convId, out var oldSecret) && oldSecret.Length > 0) {
            Crypto.Wipe(oldSecret);
        }

        _vault.ClearConvSecret(convId);
        contact.SignPubKey = contact.PendingSignPubKey;
        contact.DhPubKey = contact.PendingDhPubKey;
        contact.PendingSignPubKey = [];
        contact.PendingDhPubKey = [];
        contact.IsVerified = true;
        contact.KeyChangedAt = 0;
        _vault.SaveContact(contact);
        UpdateActiveConversationSecurityUi();
    }

    byte[]? GetOrDeriveConvKey(Contact contact) {
        var convId = contact.ConversationId ?? contact.UserId;
        if (_convKeys.TryGetValue(convId, out var cached)) return cached;

        // Try loading from vault
        var (existingSeq, storedSecret) = _vault.LoadConvState(convId);
        if (storedSecret != null) {
            _convKeys[convId] = storedSecret;
            return storedSecret;
        }

        // Derive from ECDH
        try {
            var secret = Crypto.DeriveSharedSecret(_user!.DhPrivKey, contact.DhPubKey, convId);
            _vault.SaveConvState(convId, existingSeq, secret);
            _convKeys[convId] = secret;
            return secret;
        } catch { return null; }
    }

    // ── Replay protection ─────────────────────────────────────────────────

    bool IsNewSeq(string convId, string senderId, long seq) {
        if (!_seqTracker.TryGetValue(convId, out var senders))
            _seqTracker[convId] = senders = [];
        if (senders.TryGetValue(senderId, out var last) && seq <= last) return false;
        senders[senderId] = seq;
        _seqTrackerTouched[convId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_seqTrackerTouched.Count > MaxSeqTrackerConversations) {
            var staleConvId = _seqTrackerTouched
                .OrderBy(entry => entry.Value)
                .First().Key;
            _seqTrackerTouched.Remove(staleConvId);
            _seqTracker.Remove(staleConvId);
        }
        return true;
    }

    static string BuildGroupInviteContent(GroupInfo group) =>
        GroupInvitePrefix + JsonSerializer.Serialize(new GroupInvitePayload {
            GroupId = group.GroupId,
            Name = group.Name,
            GroupKey = Convert.ToBase64String(group.GroupKey),
            MemberIds = group.MemberIds,
            OwnerId = group.OwnerId
        });

    static string BuildGroupDeleteContent(GroupInfo group) =>
        GroupDeletePrefix + JsonSerializer.Serialize(new GroupDeletePayload {
            GroupId = group.GroupId,
            OwnerId = group.OwnerId,
            Name = group.Name
        });

    internal static bool IsTrustedGroupInviteOwner(string senderUserId, string? existingOwnerId, string? inviteOwnerId) {
        if (string.IsNullOrWhiteSpace(senderUserId) || string.IsNullOrWhiteSpace(inviteOwnerId)) {
            return false;
        }

        if (!string.Equals(inviteOwnerId, senderUserId, StringComparison.Ordinal)) {
            return false;
        }

        return string.IsNullOrWhiteSpace(existingOwnerId) ||
            string.Equals(existingOwnerId, senderUserId, StringComparison.Ordinal);
    }

    static bool TryParseGroupInvite(string content, out GroupInvitePayload? invite) {
        invite = null;
        if (!content.StartsWith(GroupInvitePrefix, StringComparison.Ordinal)) return false;

        try {
            invite = JsonSerializer.Deserialize<GroupInvitePayload>(
                content[GroupInvitePrefix.Length..]);
            return invite != null;
        } catch {
            return false;
        }
    }

    static bool TryParseGroupDelete(string content, out GroupDeletePayload? payload) {
        payload = null;
        if (!content.StartsWith(GroupDeletePrefix, StringComparison.Ordinal)) return false;

        try {
            payload = JsonSerializer.Deserialize<GroupDeletePayload>(
                content[GroupDeletePrefix.Length..]);
            return payload != null &&
                !string.IsNullOrWhiteSpace(payload.GroupId) &&
                !string.IsNullOrWhiteSpace(payload.OwnerId);
        } catch {
            return false;
        }
    }

    static string BuildReceiptContent(string messageId, string conversationId, string scope, string status) =>
        DeliveryReceiptPrefix + JsonSerializer.Serialize(new DeliveryReceiptPayload(
            messageId,
            conversationId,
            scope,
            status,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    static bool TryParseDeliveryReceipt(string content, out DeliveryReceiptPayload? receipt) {
        receipt = null;
        if (!content.StartsWith(DeliveryReceiptPrefix, StringComparison.Ordinal)) return false;

        try {
            receipt = JsonSerializer.Deserialize<DeliveryReceiptPayload>(
                content[DeliveryReceiptPrefix.Length..]);
            if (receipt == null) return false;
            if (string.IsNullOrWhiteSpace(receipt.MessageId) ||
                string.IsNullOrWhiteSpace(receipt.ConversationId) ||
                string.IsNullOrWhiteSpace(receipt.Scope) ||
                string.IsNullOrWhiteSpace(receipt.Status)) {
                return false;
            }

            return receipt.Scope is ReceiptScopeDirect or ReceiptScopeGroup;
        } catch {
            return false;
        }
    }

    bool RegisterReceiptSent(string recipientId, string messageId, string status) {
        var key = $"{recipientId}|{messageId}|{status}";
        if (!_sentReceiptKeys.Add(key)) return false;

        _sentReceiptOrder.Enqueue(key);
        while (_sentReceiptOrder.Count > MaxSentReceiptKeys) {
            var staleKey = _sentReceiptOrder.Dequeue();
            _sentReceiptKeys.Remove(staleKey);
        }

        return true;
    }

    HashSet<string> GetReceiptSet(Dictionary<string, HashSet<string>> map, string messageId) {
        if (!map.TryGetValue(messageId, out var set))
            map[messageId] = set = [];
        return set;
    }

    MessageStatus? ParseReceiptStatus(string status) =>
        status.Trim().ToLowerInvariant() switch {
            ReceiptStatusReceived => MessageStatus.Delivered,
            ReceiptStatusSeen => MessageStatus.Seen,
            _ => null
        };

    void TrackReceiptMessageId(string messageId) {
        if (!_receiptMessageIds.Add(messageId)) return;

        _receiptMessageOrder.Enqueue(messageId);
        while (_receiptMessageOrder.Count > MaxReceiptCacheMessages) {
            var staleMessageId = _receiptMessageOrder.Dequeue();
            _receiptMessageIds.Remove(staleMessageId);
            _messageReceivedBy.Remove(staleMessageId);
            _messageSeenBy.Remove(staleMessageId);
        }
    }

    void RegisterIncomingReceipt(string messageId, string fromUserId, MessageStatus status) {
        TrackReceiptMessageId(messageId);

        if (status == MessageStatus.Seen) {
            GetReceiptSet(_messageReceivedBy, messageId).Add(fromUserId);
            GetReceiptSet(_messageSeenBy, messageId).Add(fromUserId);
            return;
        }

        if (status == MessageStatus.Delivered) {
            GetReceiptSet(_messageReceivedBy, messageId).Add(fromUserId);
        }
    }

    void HydrateReceiptCache(IEnumerable<Message> messages) {
        var outgoingIds = messages
            .Where(m => m.IsMine)
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (outgoingIds.Count == 0) return;

        var receipts = _vault.LoadMessageReceipts(outgoingIds);
        foreach (var receipt in receipts) {
            RegisterIncomingReceipt(receipt.MessageId, receipt.UserId, receipt.Status);
        }
    }

    string ResolveUserLabel(string userId) {
        if (_user != null && userId == _user.UserId) return $"{_user.DisplayName} (you)";
        var convContact = _convs.FirstOrDefault(c => c.ContactData?.UserId == userId)?.ContactData;
        if (convContact != null) return convContact.DisplayName;

        var contact = _vault.LoadContacts().FirstOrDefault(c => c.UserId == userId);
        return contact?.DisplayName ?? AppTelemetry.MaskUserId(userId);
    }

    Brush ColorForUserId(string userId) {
        if (_userColorCache.TryGetValue(userId, out var cached)) return cached;

        var palette = new[] {
            "#6EE7C8", "#7AA2FF", "#F6B15F", "#FF8BA7",
            "#C6A0FF", "#9BE564", "#FFB86B", "#7FD3F7",
            "#F472B6", "#34D399", "#A78BFA", "#FB923C",
            "#60A5FA", "#FBBF24", "#E879F9", "#2DD4BF"
        };
        uint hash = 2166136261;
        foreach (var ch in userId)
            hash = (hash ^ ch) * 16777619;
        var idx = (int)(hash % (uint)palette.Length);
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(palette[idx])!;
        brush.Freeze();
        _userColorCache[userId] = brush;
        return brush;
    }

    void ApplyReceiptUi(MessageViewModel vm) {
        if (!vm.IsMine) return;

        vm.SeenBy.Clear();

        _messageReceivedBy.TryGetValue(vm.Id, out var receivedBy);
        _messageSeenBy.TryGetValue(vm.Id, out var seenBy);
        receivedBy ??= [];
        seenBy ??= [];

        if (!vm.IsGroup) {
            if (seenBy.Count > 0) {
                vm.Status = MessageStatus.Seen;
                vm.DeliveryText = "seen";
                vm.DeliveryBrush = B.Blue;
                return;
            }
            if (receivedBy.Count > 0) {
                vm.Status = MessageStatus.Delivered;
                vm.DeliveryText = "received";
                vm.DeliveryBrush = B.Green;
                return;
            }

            vm.DeliveryText = vm.Status switch {
                MessageStatus.Sending => "sending",
                MessageStatus.Sent => "sent",
                MessageStatus.Failed => "failed",
                MessageStatus.Seen => "seen",
                MessageStatus.Delivered => "received",
                _ => ""
            };
            vm.DeliveryBrush = vm.Status switch {
                MessageStatus.Failed => B.Red,
                MessageStatus.Seen => B.Blue,
                MessageStatus.Delivered => B.Green,
                _ => B.Dim
            };
            return;
        }

        foreach (var userId in seenBy.OrderBy(id => ResolveUserLabel(id), StringComparer.OrdinalIgnoreCase)) {
            vm.SeenBy.Add(new SeenByChipViewModel {
                Name = ResolveUserLabel(userId),
                Color = ColorForUserId(userId)
            });
        }

        if (seenBy.Count > 0) {
            vm.DeliveryText = $"seen by {seenBy.Count}";
            vm.DeliveryBrush = B.Blue;
            return;
        }
        if (receivedBy.Count > 0) {
            vm.DeliveryText = $"received by {receivedBy.Count}";
            vm.DeliveryBrush = B.Green;
            return;
        }

        vm.DeliveryText = vm.Status switch {
            MessageStatus.Sending => "sending",
            MessageStatus.Sent => "sent",
            MessageStatus.Failed => "failed",
            _ => ""
        };
        vm.DeliveryBrush = vm.Status == MessageStatus.Failed ? B.Red : B.Dim;
    }

    void ApplyReceiptUi(string messageId) {
        var vm = MessageItems.FirstOrDefault(m => m.Id == messageId);
        if (vm != null) {
            ApplyReceiptUi(vm);
        }
    }

    async Task SendDirectReceiptAsync(Contact senderContact, Message incoming, string status) {
        if (_user == null) return;
        if (!RegisterReceiptSent(senderContact.UserId, incoming.Id, status)) return;

        var convId = senderContact.ConversationId ?? senderContact.UserId;
        var convKey = GetOrDeriveConvKey(senderContact);
        if (convKey == null) return;

        var receiptMessage = new Message {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = convId,
            SenderId = _user.UserId,
            Content = BuildReceiptContent(incoming.Id, incoming.ConversationId, ReceiptScopeDirect, status),
            IsMine = true,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        receiptMessage.SeqNum = _vault.NextSeqNum(convId);

        var payload = Crypto.EncryptDm(convKey, receiptMessage);
        var sig = Crypto.SignPayload(_user.SignPrivKey, payload, receiptMessage.SeqNum);
        var sent = await _net.SendDmAsync(senderContact.UserId, payload, sig, receiptMessage.SeqNum);
        if (!sent) {
            _vault.EnqueueOutbox(Guid.NewGuid().ToString("N"), senderContact.UserId, payload, sig, receiptMessage.SeqNum);
            AppLog.Warn("receipt", $"queued direct receipt status={status} msg={AppTelemetry.MaskUserId(incoming.Id)} to={AppTelemetry.MaskUserId(senderContact.UserId)}");
        } else {
            AppLog.Info("receipt", $"sent direct receipt status={status} msg={AppTelemetry.MaskUserId(incoming.Id)} to={AppTelemetry.MaskUserId(senderContact.UserId)}");
        }
    }

    async Task SendGroupReceiptAsync(Contact senderContact, Message incoming, string status) {
        if (_user == null) return;
        if (!RegisterReceiptSent(senderContact.UserId, incoming.Id, status)) return;

        var convId = senderContact.ConversationId ?? senderContact.UserId;
        var convKey = GetOrDeriveConvKey(senderContact);
        if (convKey == null) return;

        var receiptMessage = new Message {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = convId,
            SenderId = _user.UserId,
            Content = BuildReceiptContent(incoming.Id, incoming.ConversationId, ReceiptScopeGroup, status),
            IsMine = true,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        receiptMessage.SeqNum = _vault.NextSeqNum(convId);

        var payload = Crypto.EncryptDm(convKey, receiptMessage);
        var sig = Crypto.SignPayload(_user.SignPrivKey, payload, receiptMessage.SeqNum);
        var sent = await _net.SendDmAsync(senderContact.UserId, payload, sig, receiptMessage.SeqNum);
        if (!sent) {
            _vault.EnqueueOutbox(Guid.NewGuid().ToString("N"), senderContact.UserId, payload, sig, receiptMessage.SeqNum);
            AppLog.Warn("receipt", $"queued group receipt status={status} msg={AppTelemetry.MaskUserId(incoming.Id)} to={AppTelemetry.MaskUserId(senderContact.UserId)}");
        } else {
            AppLog.Info("receipt", $"sent group receipt status={status} msg={AppTelemetry.MaskUserId(incoming.Id)} to={AppTelemetry.MaskUserId(senderContact.UserId)}");
        }
    }

    async Task MarkActiveConversationSeenAsync() {
        if (_activeConv == null) return;

        if (!_activeConv.IsGroup && _activeConv.ContactData is Contact directContact) {
            var visibleIncoming = MessageItems.Where(m =>
                !m.IsMine &&
                !m.IsGroup &&
                string.Equals(m.ConversationId, _activeConvId, StringComparison.Ordinal)).ToList();
            foreach (var vm in visibleIncoming) {
                var incoming = new Message {
                    Id = vm.Id,
                    ConversationId = vm.ConversationId
                };
                await SendDirectReceiptAsync(directContact, incoming, ReceiptStatusSeen);
            }
            return;
        }

        if (_activeConv.IsGroup) {
            var contactsByUserId = _convs
                .Where(c => c.ContactData != null)
                .Select(c => c.ContactData!)
                .ToDictionary(c => c.UserId, StringComparer.Ordinal);
            var visibleIncoming = MessageItems.Where(m =>
                !m.IsMine &&
                m.IsGroup &&
                string.Equals(m.ConversationId, _activeConvId, StringComparison.Ordinal)).ToList();
            foreach (var vm in visibleIncoming) {
                if (!contactsByUserId.TryGetValue(vm.SenderId, out var senderContact)) continue;
                var incoming = new Message {
                    Id = vm.Id,
                    ConversationId = vm.ConversationId
                };
                await SendGroupReceiptAsync(senderContact, incoming, ReceiptStatusSeen);
            }
        }
    }

    bool TryHandleDirectSystemMessage(Contact senderContact, Message msg) {
        if (TryParseDeliveryReceipt(msg.Content, out var receipt) && receipt != null) {
            var receiptStatus = ParseReceiptStatus(receipt.Status);
            if (receiptStatus is MessageStatus.Delivered or MessageStatus.Seen) {
                var metadata = _vault.GetMessageMetadata(receipt.MessageId);
                if (metadata == null || !metadata.Value.isMine) {
                    AppLog.Warn("receipt", $"ignored receipt for unknown or incoming message {AppTelemetry.MaskUserId(receipt.MessageId)}");
                    return true;
                }

                if (!string.Equals(metadata.Value.conversationId, receipt.ConversationId, StringComparison.Ordinal)) {
                    AppLog.Warn("receipt", $"ignored receipt with mismatched conversation target msg={AppTelemetry.MaskUserId(receipt.MessageId)}");
                    return true;
                }

                if (receipt.Scope == ReceiptScopeDirect) {
                    var expectedDirectConversationId = senderContact.ConversationId ?? senderContact.UserId;
                    if (metadata.Value.convType != ConversationType.Direct ||
                        !string.Equals(receipt.ConversationId, expectedDirectConversationId, StringComparison.Ordinal)) {
                        AppLog.Warn("receipt", $"ignored direct receipt with invalid target msg={AppTelemetry.MaskUserId(receipt.MessageId)}");
                        return true;
                    }
                } else if (metadata.Value.convType != ConversationType.Group) {
                    AppLog.Warn("receipt", $"ignored group receipt with invalid target msg={AppTelemetry.MaskUserId(receipt.MessageId)}");
                    return true;
                }

                RegisterIncomingReceipt(receipt.MessageId, senderContact.UserId, receiptStatus.Value);
                _vault.UpsertMessageReceipt(receipt.MessageId, senderContact.UserId, receiptStatus.Value, receipt.Ts);
                if (receipt.Scope == ReceiptScopeDirect) {
                    _vault.UpdateMessageStatus(receipt.MessageId, receiptStatus.Value);
                }
                ApplyReceiptUi(receipt.MessageId);
                AppLog.Info("receipt", $"applied {receiptStatus.Value.ToString().ToLowerInvariant()} receipt scope={receipt.Scope} from={AppTelemetry.MaskUserId(senderContact.UserId)} msg={AppTelemetry.MaskUserId(receipt.MessageId)}");
            }
            return true;
        }

        if (TryParseGroupDelete(msg.Content, out var deletePayload) && deletePayload != null) {
            var targetGroup = _convs.FirstOrDefault(c => c.GroupData?.GroupId == deletePayload.GroupId)?.GroupData
                ?? _vault.LoadGroups().FirstOrDefault(g => g.GroupId == deletePayload.GroupId);
            if (targetGroup == null) return true;

            if (!string.Equals(targetGroup.OwnerId, senderContact.UserId, StringComparison.Ordinal)) {
                AppLog.Warn("group", $"ignored delete notice for {AppTelemetry.MaskUserId(deletePayload.GroupId)} from non-owner {AppTelemetry.MaskUserId(senderContact.UserId)}");
                return true;
            }

            RemoveGroupLocally(deletePayload.GroupId, $"group deleted: {targetGroup.Name}");
            AppLog.Info("group", $"applied group delete for {AppTelemetry.MaskUserId(deletePayload.GroupId)} from {AppTelemetry.MaskUserId(senderContact.UserId)}");
            return true;
        }

        if (!TryParseGroupInvite(msg.Content, out var invite) || invite == null || _user == null)
            return false;

        if (!invite.MemberIds.Contains(_user.UserId) || !invite.MemberIds.Contains(senderContact.UserId))
            return true;

        var existing = _convs.FirstOrDefault(c => c.GroupData?.GroupId == invite.GroupId);
        var existingGroup = existing?.GroupData ?? _vault.LoadGroups().FirstOrDefault(g => g.GroupId == invite.GroupId);
        if (!IsTrustedGroupInviteOwner(senderContact.UserId, existingGroup?.OwnerId, invite.OwnerId) ||
            !invite.MemberIds.Contains(invite.OwnerId)) {
            AppLog.Warn("group", $"ignored untrusted group invite for {AppTelemetry.MaskUserId(invite.GroupId)} from {AppTelemetry.MaskUserId(senderContact.UserId)}");
            SetSidebarStatus("ignored an untrusted group invite");
            return true;
        }

        byte[] groupKey;
        try {
            groupKey = Convert.FromBase64String(invite.GroupKey);
        } catch {
            SetSidebarStatus("received an invalid group invite");
            return true;
        }
        if (groupKey.Length != 32) {
            SetSidebarStatus("received an invalid group invite (bad key length)");
            return true;
        }

        var group = existingGroup ?? new GroupInfo {
            GroupId = invite.GroupId,
            CreatedAt = msg.Timestamp
        };

        group.Name = invite.Name;
        group.MemberIds = invite.MemberIds.Distinct().ToList();
        group.GroupKey = groupKey;
        group.OwnerId = senderContact.UserId;
        _vault.SaveGroup(group);

        if (existing == null) {
            _convs.Add(new ConvViewModel {
                Id = group.GroupId,
                DisplayName = group.Name,
                IsGroup = true,
                GroupData = group,
                IsMuted = IsConversationMuted(group.GroupId)
            });
        } else {
            existing.DisplayName = group.Name;
            existing.GroupData = group;
            existing.IsMuted = IsConversationMuted(group.GroupId);
        }

        SetSidebarStatus($"joined group: {group.Name}");
        return true;
    }

    async Task SendGroupInvitesAsync(GroupInfo group, Dictionary<string, Contact> contactsById, IEnumerable<string>? targetMembers = null) {
        if (_user == null) return;
        if (!string.Equals(group.OwnerId, _user.UserId, StringComparison.Ordinal)) {
            SetSidebarStatus("only the group owner can invite members");
            return;
        }

        var recipients = (targetMembers ?? group.MemberIds)
            .Where(id => id != _user.UserId)
            .Distinct()
            .ToList();

        foreach (var memberId in recipients) {
            if (!contactsById.TryGetValue(memberId, out var contact)) continue;
            if (!await EnsureContactKeysAsync(contact)) {
                SetSidebarStatus($"group created, but {contact.DisplayName} has not registered relay keys yet");
                continue;
            }

            var convId = contact.ConversationId ?? contact.UserId;
            var convKey = GetOrDeriveConvKey(contact);
            if (convKey == null) {
                SetSidebarStatus($"group created, but invite encryption failed for {contact.DisplayName}");
                continue;
            }

            var inviteMessage = new Message {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = convId,
                SenderId = _user.UserId,
                Content = BuildGroupInviteContent(group),
                IsMine = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            inviteMessage.SeqNum = _vault.NextSeqNum(convId);

            var payload = Crypto.EncryptDm(convKey, inviteMessage);
            var sig = Crypto.SignPayload(_user.SignPrivKey, payload, inviteMessage.SeqNum);
            var sent = await _net.SendDmAsync(contact.UserId, payload, sig, inviteMessage.SeqNum);
            if (!sent) {
                _vault.EnqueueOutbox(Guid.NewGuid().ToString("N"), contact.UserId, payload, sig, inviteMessage.SeqNum);
                SetSidebarStatus($"group invite queued for {contact.DisplayName}");
            }
        }
    }

    // ── Add contact / group ────────────────────────────────────────────────

    void BtnAddContact_Click(object s, RoutedEventArgs e) {
        AddOverlay.Visibility = Visibility.Visible;
        AddContactId.Clear();
        AddContactName.Clear();
        AddStatus.Visibility = Visibility.Collapsed;
        AddStatusPanel.Visibility = Visibility.Collapsed;
        ShowAddDmTab();
    }

    void BtnQuickNewGroup_Click(object s, RoutedEventArgs e) {
        AddOverlay.Visibility = Visibility.Visible;
        AddStatus.Visibility = Visibility.Collapsed;
        AddStatusPanel.Visibility = Visibility.Collapsed;
        _groupInviteMode = false;
        ShowAddGroupTab();
    }

    void BtnGroupInvite_Click(object s, RoutedEventArgs e) {
        if (_activeConv?.GroupData == null) {
            SetSidebarStatus("open a group first");
            return;
        }

        GroupMenuPopup.IsOpen = false;
        AddOverlay.Visibility = Visibility.Visible;
        AddStatus.Visibility = Visibility.Collapsed;
        AddStatusPanel.Visibility = Visibility.Collapsed;
        _groupInviteMode = true;
        ShowAddGroupTab();
    }

    void TabAddDm_Click(object s, RoutedEventArgs e) => ShowAddDmTab();
    void TabAddGroup_Click(object s, RoutedEventArgs e) {
        _groupInviteMode = false;
        ShowAddGroupTab();
    }

    void ShowAddDmTab() {
        AddDmForm.Visibility = Visibility.Visible;
        AddGroupForm.Visibility = Visibility.Collapsed;
        TabAddDm.Content = "Add Friend";
        TabAddGroup.Content = "New Group";
        TabAddDm.Style = (Style)FindResource("AccentBtn");
        TabAddGroup.Style = (Style)FindResource("GroupBtn");
        TabAddDm.BorderBrush = (Brush)FindResource("TealAccent");
        TabAddGroup.BorderBrush = (Brush)FindResource("Border");
        AddStatus.Visibility = Visibility.Collapsed;
        AddStatusPanel.Visibility = Visibility.Collapsed;
        _groupInviteMode = false;
    }

    void ShowAddGroupTab() {
        AddDmForm.Visibility = Visibility.Collapsed;
        AddGroupForm.Visibility = Visibility.Visible;
        TabAddDm.Content = "Add Friend";
        TabAddGroup.Content = "New Group";
        TabAddDm.Style = (Style)FindResource("AccentBtn");
        TabAddGroup.Style = (Style)FindResource("GroupBtn");
        TabAddGroup.BorderBrush = (Brush)FindResource("GoldAccent");
        TabAddDm.BorderBrush = (Brush)FindResource("Border");
        AddStatus.Visibility = Visibility.Collapsed;
        AddStatusPanel.Visibility = Visibility.Collapsed;

        GroupMemberIds.Clear();
        if (_groupInviteMode && _activeConv?.IsGroup == true && _activeConv.GroupData != null) {
            _groupInviteMode = true;
            GroupModeHint.Text = $"Invite members to {_activeConv.GroupData.Name}.";
            NewGroupName.Text = _activeConv.GroupData.Name;
            NewGroupName.IsEnabled = false;
            BtnConfirmCreateGroup.Content = "Invite members";
            return;
        }

        _groupInviteMode = false;
        GroupModeHint.Text = "Create a solo group now and invite people later.";
        NewGroupName.IsEnabled = true;
        NewGroupName.Clear();
        BtnConfirmCreateGroup.Content = "Create group";
    }

    void BtnCloseOverlay_Click(object s, RoutedEventArgs e) =>
        AddOverlay.Visibility = Visibility.Collapsed;

    void BtnConfirmAdd_Click(object s, RoutedEventArgs e) =>
        RunUiTask(AddContactAsync, "add contact", showSidebarErrors: false, onError: message => ShowAddStatus(message));

    async Task AddContactAsync() {
        var uid = AddContactId.Text.Trim();
        var name = AddContactName.Text.Trim();

        if (string.IsNullOrEmpty(uid)) { ShowAddStatus("enter a user id"); return; }
        if (!IsValidUserId(uid)) { ShowAddStatus("enter a valid user id"); return; }
        if (string.IsNullOrEmpty(name)) { ShowAddStatus("enter a display name"); return; }
        if (uid == _user!.UserId) { ShowAddStatus("that's your own id"); return; }
        if (_vault.LoadContacts().Any(c => c.UserId == uid)) { ShowAddStatus("already in contacts"); return; }

        ShowAddStatus("fetching public keys from relay...");

        // Fetch keys from relay
        var keys = await _net.GetUserKeysAsync(uid);
        if (keys == null) {
            // Add anyway with placeholder keys (they'll connect later)
            ShowAddStatus("user not online — added offline, keys will sync when they connect", false);
        }

        var contact = new Contact {
            UserId = uid,
            DisplayName = name,
            SignPubKey = keys?.signPub ?? [],
            DhPubKey = keys?.dhPub ?? [],
            ConversationId = $"dm:{_user.UserId}:{uid}",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Normalize conversation ID (same for both sides)
        var ids = new[] { _user.UserId, uid }.OrderBy(x => x).ToArray();
        contact.ConversationId = $"dm:{ids[0]}:{ids[1]}";

        _vault.SaveContact(contact);

        var conv = new ConvViewModel {
            Id = contact.ConversationId,
            DisplayName = name,
            IsGroup = false,
            ContactData = contact,
            IsMuted = false
        };
        _convs.Add(conv);
        AddOverlay.Visibility = Visibility.Collapsed;

        // Open new conversation
        ConvList.SelectedItem = conv;
    }

    void BtnCreateGroup_Click(object s, RoutedEventArgs e) =>
        RunUiTask(_groupInviteMode ? InviteMembersToActiveGroupAsync : CreateGroupAsync,
            _groupInviteMode ? "invite group members" : "create group",
            showSidebarErrors: false,
            onError: message => ShowAddStatus(message));

    async Task CreateGroupAsync() {
        var name = NewGroupName.Text.Trim();
        if (string.IsNullOrEmpty(name)) { ShowAddStatus("enter a group name"); return; }

        var memberLines = GroupMemberIds.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var memberIds = memberLines.Select(m => m.Trim())
                                   .Where(m => !string.IsNullOrEmpty(m) && m != _user!.UserId)
                                   .Distinct().ToList();

        var contactsById = _vault.LoadContacts().ToDictionary(c => c.UserId);
        var missingContacts = memberIds.Where(id => !contactsById.ContainsKey(id)).Distinct().ToList();
        if (missingContacts.Count > 0) {
            ShowAddStatus("add all group members as direct contacts first");
            return;
        }

        memberIds.Add(_user!.UserId); // Always include self

        var group = new GroupInfo {
            GroupId = Guid.NewGuid().ToString("N"),
            Name = name,
            MemberIds = memberIds,
            GroupKey = RandomNumberGenerator.GetBytes(32), // Random 256-bit group key
            OwnerId = _user.UserId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _vault.SaveGroup(group);
        var conv = new ConvViewModel {
            Id = group.GroupId,
            DisplayName = name,
            IsGroup = true,
            GroupData = group,
            IsMuted = false
        };
        _convs.Add(conv);
        AddOverlay.Visibility = Visibility.Collapsed;
        ConvList.SelectedItem = conv;

        var inviteTargets = memberIds.Where(id => id != _user.UserId).ToList();
        if (inviteTargets.Count > 0) {
            await SendGroupInvitesAsync(group, contactsById, inviteTargets);
        } else {
            SetSidebarStatus($"created {group.Name}. invite members later from the Group tab.");
        }
    }

    async Task InviteMembersToActiveGroupAsync() {
        if (_activeConv?.GroupData is not GroupInfo group || _user == null) {
            ShowAddStatus("open a group first");
            return;
        }
        if (!IsCurrentUserGroupOwner(group)) {
            ShowAddStatus("only the group owner can invite members");
            return;
        }

        var memberLines = GroupMemberIds.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var requested = memberLines.Select(m => m.Trim())
                                   .Where(m => !string.IsNullOrEmpty(m) && m != _user.UserId)
                                   .Distinct()
                                   .ToList();
        if (requested.Count == 0) {
            ShowAddStatus("enter at least one member user id");
            return;
        }

        var contactsById = _vault.LoadContacts().ToDictionary(c => c.UserId);
        var missingContacts = requested.Where(id => !contactsById.ContainsKey(id)).Distinct().ToList();
        if (missingContacts.Count > 0) {
            ShowAddStatus("add those users as direct contacts first");
            return;
        }

        var newMembers = requested.Where(id => !group.MemberIds.Contains(id)).Distinct().ToList();
        if (newMembers.Count == 0) {
            ShowAddStatus("all listed users are already in this group", false);
            return;
        }

        group.MemberIds = group.MemberIds.Concat(newMembers).Distinct().ToList();
        _vault.SaveGroup(group);
        await SendGroupInvitesAsync(group, contactsById, newMembers);

        AddOverlay.Visibility = Visibility.Collapsed;
        SetSidebarStatus($"invited {newMembers.Count} member(s) to {group.Name}");
    }

    bool IsCurrentUserGroupOwner(GroupInfo group) =>
        _user != null &&
        !string.IsNullOrWhiteSpace(group.OwnerId) &&
        string.Equals(group.OwnerId, _user.UserId, StringComparison.Ordinal);

    void RefreshActiveGroupMenu() {
        if (_activeConv?.GroupData is not GroupInfo group) return;

        GroupMenuTitleText.Text = group.Name;
        GroupMenuOwnerText.Text = string.IsNullOrWhiteSpace(group.OwnerId)
            ? "owner unknown"
            : $"owner: {ResolveUserLabel(group.OwnerId)}";
        GroupMenuMembersText.Text = "members: " + string.Join(", ",
            group.MemberIds
                .Distinct(StringComparer.Ordinal)
                .Select(ResolveUserLabel)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        BtnGroupMenuDelete.Visibility = IsCurrentUserGroupOwner(group)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void BtnGroupMenu_Click(object s, RoutedEventArgs e) {
        if (_activeConv?.GroupData == null) return;
        RefreshActiveGroupMenu();
        GroupMenuPopup.IsOpen = !GroupMenuPopup.IsOpen;
    }

    void BtnMuteConversation_Click(object s, RoutedEventArgs e) {
        if (_activeConv == null) return;

        var next = !_activeConv.IsMuted;
        SetConversationMuted(_activeConv, next);
        SetSidebarStatus(next
            ? $"muted notifications for {_activeConv.DisplayName}"
            : $"unmuted notifications for {_activeConv.DisplayName}");
        AppLog.Info("notify", $"{(next ? "muted" : "unmuted")} {AppTelemetry.DescribeConversation(_activeConv.Id, _activeConv.IsGroup)}");
    }

    void BtnCloseGroupMenu_Click(object s, RoutedEventArgs e) =>
        GroupMenuPopup.IsOpen = false;

    async Task SendGroupDeleteNoticeAsync(GroupInfo group) {
        if (_user == null) return;

        var contactsById = _vault.LoadContacts().ToDictionary(c => c.UserId);
        foreach (var memberId in group.MemberIds.Where(id => id != _user.UserId).Distinct()) {
            if (!contactsById.TryGetValue(memberId, out var contact)) continue;
            if (!await EnsureContactKeysAsync(contact)) continue;

            var convId = contact.ConversationId ?? contact.UserId;
            var convKey = GetOrDeriveConvKey(contact);
            if (convKey == null) continue;

            var notice = new Message {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = convId,
                SenderId = _user.UserId,
                Content = BuildGroupDeleteContent(group),
                IsMine = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            notice.SeqNum = _vault.NextSeqNum(convId);

            var payload = Crypto.EncryptDm(convKey, notice);
            var sig = Crypto.SignPayload(_user.SignPrivKey, payload, notice.SeqNum);
            var sent = await _net.SendDmAsync(contact.UserId, payload, sig, notice.SeqNum);
            if (!sent) {
                _vault.EnqueueOutbox(Guid.NewGuid().ToString("N"), contact.UserId, payload, sig, notice.SeqNum);
            }
        }
    }

    void RemoveGroupLocally(string groupId, string statusMessage) {
        _vault.DeleteGroupConversation(groupId);

        var conv = _convs.FirstOrDefault(c => c.GroupData?.GroupId == groupId);
        if (conv != null) {
            _convs.Remove(conv);
        }

        if (string.Equals(_activeConvId, groupId, StringComparison.Ordinal)) {
            _activeConv = null;
            _activeConvId = "";
            _messages.Clear();
            SetConversationSurfaceState(false);
            UpdateActiveConversationSecurityUi();
        }

        SetSidebarStatus(statusMessage);
    }

    void BtnLeaveGroup_Click(object s, RoutedEventArgs e) =>
        RunUiTask(LeaveGroupAsync, "leave group", showSidebarErrors: false);

    async Task LeaveGroupAsync() {
        if (_activeConv?.GroupData is not GroupInfo group || _user == null) return;

        GroupMenuPopup.IsOpen = false;
        var action = IsCurrentUserGroupOwner(group)
            ? $"Leave {group.Name} on this device?\n\nYou created this group. Leaving removes it only for you. Use Delete if you want to close it for everyone."
            : $"Leave {group.Name} on this device?\n\nThis only removes the group from this local app.";
        var confirm = ActionConfirmWindow.Show(
            this,
            $"Leave {group.Name}?",
            action,
            "Leave group",
            "You can still delete the whole group later if you are the owner.",
            destructive: false);
        if (!confirm) return;

        await Task.CompletedTask;
        RemoveGroupLocally(group.GroupId, $"left {group.Name}");
    }

    void BtnDeleteGroup_Click(object s, RoutedEventArgs e) =>
        RunUiTask(DeleteGroupAsync, "delete group", showSidebarErrors: false);

    async Task DeleteGroupAsync() {
        if (_activeConv?.GroupData is not GroupInfo group) return;
        if (!IsCurrentUserGroupOwner(group)) {
            SetSidebarStatus("only the group creator can delete this group");
            return;
        }

        GroupMenuPopup.IsOpen = false;
        var confirm = ActionConfirmWindow.Show(
            this,
            $"Delete {group.Name}?",
            $"Delete {group.Name} for all current members?\n\nThey will stop seeing it once your delete notice reaches them.",
            "Delete group",
            "This sends a group deletion notice to the current members.",
            destructive: true);
        if (!confirm) return;

        await SendGroupDeleteNoticeAsync(group);
        RemoveGroupLocally(group.GroupId, $"deleted {group.Name}");
    }

    void ShowAddStatus(string msg, bool isError = true) {
        AddStatus.Text = msg;
        AddStatus.Foreground = isError ? (Brush)FindResource("Red") : (Brush)FindResource("Dim");
        AddStatus.Visibility = Visibility.Visible;
        AddStatusPanel.Visibility = Visibility.Visible;
    }

    // ── My ID copy ────────────────────────────────────────────────────────

    void BtnMyId_Click(object s, RoutedEventArgs e) {
        if (_user == null) return;
        Clipboard.SetText(_user.UserId);
        var prev = BtnMyId.Content;
        BtnMyId.Content = "Copied";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { BtnMyId.Content = prev; t.Stop(); };
        t.Start();
    }

    void BtnSecurityReview_Click(object s, RoutedEventArgs e) =>
        RunUiTask(SecurityReviewAsync, "review contact security", showSidebarErrors: false);

    async Task SecurityReviewAsync() {
        if (_activeConv?.ContactData is not Contact contact || _user == null) return;

        if (contact.SignPubKey.Length == 0 || contact.DhPubKey.Length == 0) {
            await EnsureContactKeysAsync(contact);
            if (contact.SignPubKey.Length == 0 || contact.DhPubKey.Length == 0) {
                MessageBox.Show(
                    $"{contact.DisplayName} has not published relay keys yet, so there is nothing to verify.",
                    "Contact Security",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        if (contact.HasPendingKeyChange) {
            var current = ComputeSafetyNumber(contact);
            var pending = ComputeSafetyNumber(contact, usePendingKeys: true);
            var result = MessageBox.Show(
                $"The relay reported new keys for {contact.DisplayName}.\n\n" +
                $"Current pinned safety number:\n{current}\n\n" +
                $"New reported safety number:\n{pending}\n\n" +
                "Only accept this change after comparing the new safety number with your contact over another trusted channel.\n\n" +
                "Click Yes only if you verified it yourself.",
                "Security Review Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes) {
                AcceptPendingContactKeys(contact);
                SetSidebarStatus($"{contact.DisplayName} verified with the new safety number");
            }

            return;
        }

        var safetyNumber = ComputeSafetyNumber(contact);
        if (contact.IsVerified) {
            MessageBox.Show(
                $"{contact.DisplayName} is currently verified.\n\nSafety number:\n{safetyNumber}",
                "Verified Contact",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var verify = MessageBox.Show(
            $"Compare this safety number with {contact.DisplayName} over another trusted channel before marking them verified.\n\n{safetyNumber}",
            "Verify Contact",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (verify == MessageBoxResult.Yes) {
            contact.IsVerified = true;
            _vault.SaveContact(contact);
            SetSidebarStatus($"{contact.DisplayName} marked as verified");
            UpdateActiveConversationSecurityUi();
        }
    }

    // ── NUKE ──────────────────────────────────────────────────────────────

    void BtnNuke_Click(object s, RoutedEventArgs e) {
        NukeConfirm.Clear();
        NukeOverlay.Visibility = Visibility.Visible;
        NukeConfirm.Focus();
    }

    void BtnCancelNuke_Click(object s, RoutedEventArgs e) =>
        NukeOverlay.Visibility = Visibility.Collapsed;

    void BtnConfirmNuke_Click(object s, RoutedEventArgs e) =>
        RunUiTask(ConfirmNukeAsync, "nuke vault", showSidebarErrors: false);

    async Task ConfirmNukeAsync() {
        if (NukeConfirm.Text != "NUKE") {
            NukeConfirm.BorderBrush = (Brush)FindResource("Red");
            return;
        }

        BtnConfirmNuke_Click_inner(); // disable UI immediately
        NukeOverlay.Visibility = Visibility.Collapsed;

        // Send nuke warning to all contacts (best effort, fire-and-forget)
        if (_net.IsConnected && _user != null) {
            var contacts = _vault.LoadContacts();
            foreach (var contact in contacts) {
                try {
                    var msg = new Message {
                        ConversationId = contact.ConversationId ?? "",
                        SenderId = _user.UserId,
                        Content = $"[SYSTEM] {_user.DisplayName} has detonated their vault. This conversation is over.",
                        SeqNum = _vault.NextSeqNum(contact.ConversationId ?? contact.UserId)
                    };
                    var convKey = GetOrDeriveConvKey(contact);
                    if (convKey != null) {
                        var payload = Crypto.EncryptDm(convKey, msg);
                        var sig = Crypto.SignPayload(_user.SignPrivKey, payload, msg.SeqNum);
                        await _net.SendDmAsync(contact.UserId, payload, sig, msg.SeqNum);
                    }
                } catch (Exception ex) {
                    AppLog.Warn("nuke", $"failed to notify {contact.DisplayName}: {ex.Message}");
                }
            }
        }

        // Disconnect
        await _net.DisposeAsync();

        // Clear session
        Session.Clear();

        // Nuke vault (3-pass overwrite)
        foreach (var key in _convKeys.Values) Crypto.Wipe(key);
        _convKeys.Clear();
        _vault.Nuke();

        // Kill the app
        _exitRequested = true;
        Application.Current.Shutdown();
    }

    void BtnConfirmNuke_Click_inner() {
        IsEnabled = false; // Prevent any further interaction
    }

    // ── Helper: ViewModel factory ─────────────────────────────────────────

    MessageViewModel ToViewModel(Message msg, string senderName) {
        var vm = new MessageViewModel {
            Id = msg.Id,
            ConversationId = msg.ConversationId,
            SenderId = msg.SenderId,
            SenderLabel = msg.IsMine ? (_user?.DisplayName ?? "You") : senderName,
            Content = msg.Content,
            Timestamp = msg.Timestamp,
            SeqNum = msg.SeqNum,
            IsMine = msg.IsMine,
            IsGroup = msg.ConvType == ConversationType.Group,
            Status = msg.Status
        };
        var renderProfile = MsgBodyHelper.BuildRenderProfile(msg.Content, GetCurrentChatFontSize());
        vm.UseBubblelessLayout = renderProfile.UseBubblelessLayout;
        vm.SenderColorBrush = msg.IsMine
            ? B.AmberDim
            : msg.ConvType == ConversationType.Group
                ? ColorForUserId(msg.SenderId)
                : B.Green;
        ApplyReceiptUi(vm);
        return vm;
    }

    // ── Helper: find ScrollViewer in ListBox ──────────────────────────────

    static ScrollViewer? GetScrollViewer(DependencyObject o) {
        if (o is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) {
            var child = VisualTreeHelper.GetChild(o, i);
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    // ── Cleanup on close ──────────────────────────────────────────────────

    protected override void OnClosing(CancelEventArgs e) {
        if (!_exitRequested && _shellPreferences.CloseToTrayOnClose && !AppRuntime.IsTestMode) {
            if (HideToTray(showHint: true)) {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e) {
        _uiLifetimeCts.Cancel();
        _outboxTimer?.Stop();
        if (_notifyIcon != null) {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        try {
            _net.DisposeAsync().AsTask().GetAwaiter().GetResult();
        } catch (Exception ex) {
            AppLog.Warn("shutdown", $"network cleanup failed: {ex.Message}");
        }
        foreach (var key in _convKeys.Values) Crypto.Wipe(key);
        _vault.Dispose();
        AppLog.Info("shutdown", "window closed");
        base.OnClosed(e);
    }
}
