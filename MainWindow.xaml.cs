// ═══════════════════════════════════════════════════════════════════════════
//  CIPHER — MainWindow
//  Real-time E2E encrypted chat. All sensitive work is in Core.cs.
//  This file handles: UI state, message rendering, lazy history,
//  scroll management, contact management, nuke, stay-logged-in.
// ═══════════════════════════════════════════════════════════════════════════
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Cipher;

// ── Brush cache — avoids allocating new brushes on every property access ──────
static class B {
    static readonly BrushConverter Cvt = new();
    static SolidColorBrush Make(string hex) {
        var b = (SolidColorBrush)Cvt.ConvertFromString(hex)!;
        b.Freeze(); // Frozen = thread-safe + GC-friendly
        return b;
    }
    public static readonly SolidColorBrush Green      = Make("#00FF41");
    public static readonly SolidColorBrush DimGreen   = Make("#00882A");
    public static readonly SolidColorBrush Amber      = Make("#FFB000");
    public static readonly SolidColorBrush AmberDim   = Make("#FF8800");
    public static readonly SolidColorBrush AmberText  = Make("#FFD066");
    public static readonly SolidColorBrush TextGreen  = Make("#00AA22");
    public static readonly SolidColorBrush Dim        = Make("#444444");
    public static readonly SolidColorBrush DimName    = Make("#666666");
    public static readonly SolidColorBrush DimOnline  = Make("#333333");
    public static readonly SolidColorBrush NameOnline = Make("#AAFFAA");
    public static readonly SolidColorBrush Red        = Make("#FF3333");
    public static readonly SolidColorBrush Transparent = Make("#00000000");
}

// ── View Models ──────────────────────────────────────────────────────────────

public class ConvViewModel : INotifyPropertyChanged {
    string _displayName = "";
    bool _isOnline;
    int _unreadCount;
    bool _isGroup;

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
        set { _unreadCount = value; Notify(); Notify(nameof(HasUnread)); }
    }
    public bool IsGroup { get => _isGroup; set { _isGroup = value; Notify(); } }

    public string StatusChar  => IsGroup ? "◈" : (IsOnline ? "●" : "○");
    public Brush  StatusBrush => IsOnline ? B.Green : B.DimOnline;
    public Brush  NameBrush   => IsOnline ? B.NameOnline : B.DimName;
    public bool   HasUnread   => UnreadCount > 0;

    public Contact?   ContactData { get; set; }
    public GroupInfo? GroupData   { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new(p));
}

public class MessageViewModel : INotifyPropertyChanged {
    MessageStatus _status;

    public string Id             { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SenderId       { get; set; } = "";
    public string SenderLabel    { get; set; } = "";
    public string Content        { get; set; } = "";
    public long   Timestamp      { get; set; }
    public long   SeqNum         { get; set; }
    public bool   IsMine         { get; set; }
    public MessageStatus Status {
        get => _status;
        set { _status = value; Notify(); Notify(nameof(StatusChar)); Notify(nameof(StatusBrush)); }
    }

    public string TimeStr    => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp)
                                              .LocalDateTime.ToString("HH:mm");
    public string BulletChar => IsMine ? "▶" : "◀";
    public Brush  BulletBrush => IsMine ? B.Amber : B.DimGreen;
    public Brush  TextBrush   => IsMine ? B.AmberText : B.Green;
    public Brush  NameBrush   => IsMine ? B.AmberDim : B.TextGreen;
    public Brush  RowBg       => B.Transparent;
    public string StatusChar  => IsMine ? Status switch {
        MessageStatus.Sending   => "◌",
        MessageStatus.Sent      => "◎",
        MessageStatus.Delivered => "●",
        MessageStatus.Failed    => "✗",
        _ => ""
    } : "";
    public Brush StatusBrush => Status == MessageStatus.Failed    ? B.Red  :
                                 Status == MessageStatus.Delivered ? B.Green : B.Dim;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new(p));
}

// ── Main Window ───────────────────────────────────────────────────────────────

record GroupInvitePayload(string GroupId, string Name, string GroupKey, List<string> MemberIds);

public partial class MainWindow : Window {
    const string DefaultRelayUrl = "https://cipher-relay.onrender.com";
    const string GroupInvitePrefix = "[cipher-group-invite]";

    // ── State ─────────────────────────────────────────────────────────────
    Vault _vault = new();
    NetworkClient _net = new();
    LocalUser? _user;

    readonly ObservableCollection<ConvViewModel> _convs = [];
    readonly ObservableCollection<MessageViewModel> _messages = [];

    ConvViewModel? _activeConv;
    string _activeConvId = "";

    // Lazy loading state
    int _msgOffset = 0;
    int _msgTotal = 0;
    bool _loadingHistory = false;
    bool _isNearBottom = true;
    ScrollViewer? _msgScrollViewer;

    // Per-conversation shared secrets (cached in memory, not re-derived every message)
    readonly Dictionary<string, byte[]> _convKeys = [];

    // Sequence number tracking per sender per conversation (replay protection on client)
    readonly Dictionary<string, Dictionary<string, long>> _seqTracker = [];

    // Reconnect outbox flush timer
    DispatcherTimer? _outboxTimer;

    // ── Init ──────────────────────────────────────────────────────────────

    public MainWindow() {
        InitializeComponent();
        MessageList.ItemsSource = _messages;
        ConvList.ItemsSource = _convs;

        // Hook scroll event for lazy loading (set after list renders)
        MessageList.Loaded += (_, _) => {
            _msgScrollViewer = GetScrollViewer(MessageList);
            if (_msgScrollViewer != null)
                _msgScrollViewer.ScrollChanged += OnScrollChanged;
        };

        // Outbox retry timer: every 5 seconds try to flush pending messages
        _outboxTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _outboxTimer.Tick += async (_, _) => await FlushOutboxAsync();
        _outboxTimer.Start();

        // Try auto-login if session saved
        Loaded += async (_, _) => await TryAutoLoginAsync();
    }

    // ── Auto-login ────────────────────────────────────────────────────────

    async Task TryAutoLoginAsync() {
        if (!System.IO.File.Exists(Vault.DefaultVaultPath)) {
            ShowRegisterTab();
            SetAuthStatus("", false);
            return;
        }

        // Show login as default when vault exists
        ShowLoginTab();

        // Try DPAPI session key
        var sessionKey = Session.TryLoad();
        if (sessionKey != null) {
            SetAuthStatus("unlocking vault...", false);
            try {
                _vault.Open(Vault.DefaultVaultPath, sessionKey);
                _user = _vault.LoadIdentity();
                if (_user != null) {
                    LoginServerUrl.Text = _user.ServerUrl;
                    await StartChatAsync();
                    return;
                }
            } catch {
                Session.Clear();
                _vault.Dispose();
                _vault = new Vault();
            }
        }

        // Load saved server URL if vault exists
        var saltExists = System.IO.File.Exists(Vault.SaltPath);
        SetAuthStatus(saltExists ? "" : "no vault found — create one", false);
        if (!saltExists) ShowRegisterTab();
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
            LoginServerUrl.Text = DefaultRelayUrl;
    }

    void ShowRegisterTab() {
        LoginForm.Visibility = Visibility.Collapsed;
        RegisterForm.Visibility = Visibility.Visible;
        TabRegister.BorderBrush = (Brush)FindResource("Green");
        TabLogin.BorderBrush = (Brush)FindResource("Border");
        if (string.IsNullOrEmpty(RegServerUrl.Text))
            RegServerUrl.Text = DefaultRelayUrl;
    }

    void LoginPass_KeyDown(object s, KeyEventArgs e) {
        if (e.Key == Key.Enter) BtnLogin_Click(s, e);
    }

    async void BtnLogin_Click(object s, RoutedEventArgs e) {
        var pass = LoginPass.Password;
        var serverUrl = NormalizeServerUrl(LoginServerUrl.Text);
        if (string.IsNullOrEmpty(pass)) { SetAuthStatus("enter password", true); return; }
        if (!System.IO.File.Exists(Vault.SaltPath)) { SetAuthStatus("no vault found — create one", true); return; }

        if (!IsValidServerUrl(serverUrl)) { SetAuthStatus("enter a valid http(s) server url", true); return; }
        SetAuthStatus("deriving key (this takes ~2s)...", false);
        BtnLogin.IsEnabled = false;

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

            Session.Save(key); // Save for auto-login
            await StartChatAsync();
        } catch (Exception ex) {
            _vault.Dispose();
            _vault = new Vault();
            SetAuthStatus($"error: {ex.Message}", true);
            BtnLogin.IsEnabled = true;
        }
    }

    async void BtnRegister_Click(object s, RoutedEventArgs e) {
        var name = RegName.Text.Trim();
        var pass = RegPass.Password;
        var pass2 = RegPass2.Password;
        var server = RegServerUrl.Text.Trim();
        var serverUrl = NormalizeServerUrl(server);

        if (string.IsNullOrEmpty(name)) { SetAuthStatus("enter a display name", true); return; }
        if (pass != pass2) { SetAuthStatus("passwords do not match", true); return; }
        if (!IsValidServerUrl(serverUrl)) { SetAuthStatus("enter a valid http(s) server url", true); return; }
        // No other password requirements — Argon2id handles security

        SetAuthStatus("generating keys & vault (takes ~3s)...", false);
        BtnRegister.IsEnabled = false;

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

            Session.Save(key);
            await StartChatAsync();
        } catch (Exception ex) {
            SetAuthStatus($"error: {ex.Message}", true);
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

    // ── Start chat ────────────────────────────────────────────────────────

    async Task StartChatAsync() {
        AuthPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;

        // Show user ID in header (truncated)
        BtnMyId.Content = $"[id: {_user!.UserId[..8]}…]";

        // Load contacts and groups
        LoadConversations();

        // Connect to relay
        WireNetworkEvents();
        await _net.ConnectAsync(_user);
    }

    void LoadConversations() {
        _convs.Clear();
        var contacts = _vault.LoadContacts();
        foreach (var c in contacts) {
            _convs.Add(new ConvViewModel {
                Id = c.ConversationId ?? c.UserId,
                DisplayName = c.DisplayName,
                IsGroup = false,
                ContactData = c
            });
        }
        var groups = _vault.LoadGroups();
        foreach (var g in groups) {
            _convs.Add(new ConvViewModel {
                Id = g.GroupId,
                DisplayName = $"# {g.Name}",
                IsGroup = true,
                GroupData = g
            });
        }
    }

    // ── Network event wiring ─────────────────────────────────────────────

    void WireNetworkEvents() {
        _net.OnConnected += () => Dispatcher.InvokeAsync(() => {
            ConnDot.Text = "●";
            ConnDot.Foreground = (Brush)FindResource("Green");
            // Announce presence to all contacts
            var ids = _convs.Where(c => !c.IsGroup).Select(c => c.ContactData!.UserId).ToList();
            _ = _net.AnnouncePresenceAsync(ids);
            foreach (var contact in _convs.Where(c => !c.IsGroup)
                                          .Select(c => c.ContactData!)
                                          .Where(c => c.SignPubKey.Length == 0 || c.DhPubKey.Length == 0))
                _ = EnsureContactKeysAsync(contact);
            _ = FlushOutboxAsync();
        });

        _net.OnDisconnected += () => Dispatcher.InvokeAsync(() => {
            ConnDot.Text = "●";
            ConnDot.Foreground = (Brush)FindResource("Red");
            foreach (var c in _convs.Where(c => !c.IsGroup))
                c.IsOnline = false;
        });

        _net.OnUserOnline += uid => Dispatcher.InvokeAsync(() => {
            var conv = _convs.FirstOrDefault(c => c.ContactData?.UserId == uid);
            if (conv?.ContactData is { } contact) {
                conv.IsOnline = true;
                _ = EnsureContactKeysAsync(contact);
            }
        });

        _net.OnMessage += async (senderId, payload, sig, seq, ts) => {
            await Dispatcher.InvokeAsync(async () =>
                await HandleIncomingDmAsync(senderId, payload, sig, seq, ts));
        };

        _net.OnGroupMessage += async (groupId, senderId, payload, sig, seq, ts) => {
            await Dispatcher.InvokeAsync(async () =>
                await HandleIncomingGroupAsync(groupId, senderId, payload, sig, seq, ts));
        };

        _net.OnError += msg => Dispatcher.InvokeAsync(() => {
            // Non-fatal: show in status area
            SidebarStatus.Text = $"! {msg}";
        });
    }

    // ── Incoming message handlers ─────────────────────────────────────────

    async Task HandleIncomingDmAsync(string senderId, string payload, string sig, long seq, long ts) {
        // Find contact
        var conv = _convs.FirstOrDefault(c => c.ContactData?.UserId == senderId);
        if (conv == null) return; // Unknown sender — ignore

        var contact = conv.ContactData!;
        if (!await EnsureContactKeysAsync(contact)) return;

        // Verify signature (server already checked, client double-checks)
        if (!Crypto.Verify(contact.SignPubKey, $"{payload}:{seq}", sig)) return;

        // Replay protection
        if (!IsNewSeq(conv.Id, contact.UserId, seq)) return;

        // Get/derive shared secret
        var convKey = GetOrDeriveConvKey(contact);
        if (convKey == null) return;

        var msg = Crypto.DecryptDm(convKey, senderId, payload);
        if (msg == null) return;

        msg.ConversationId = conv.Id;
        msg.IsMine = false;
        if (TryHandleDirectSystemMessage(contact, msg)) return;

        // Deduplicate
        if (_vault.MessageExists(msg.Id)) return;

        _vault.SaveMessage(msg);

        // If this conversation is active, show the message
        if (conv.Id == _activeConvId) {
            var vm = ToViewModel(msg, contact.DisplayName);
            AddMessageToList(vm);
        } else {
            conv.UnreadCount++;
        }

        await Task.CompletedTask;
    }

    async Task HandleIncomingGroupAsync(string groupId, string senderId, string payload, string sig, long seq, long ts) {
        // Handle group prefix in payload from server
        var actualPayload = payload.StartsWith($"GROUP:{groupId}:")
            ? payload[$"GROUP:{groupId}:".Length..] : payload;

        var conv = _convs.FirstOrDefault(c => c.GroupData?.GroupId == groupId);
        if (conv == null) return;

        var group = conv.GroupData!;

        // Find sender contact for signature verification
        var senderContact = _vault.LoadContacts().FirstOrDefault(c => c.UserId == senderId);
        if (senderContact == null) return;
        if (!await EnsureContactKeysAsync(senderContact)) return;
        if (!Crypto.Verify(senderContact.SignPubKey, $"{actualPayload}:{seq}", sig)) return;
        if (!IsNewSeq(groupId, senderId, seq)) return;

        var msg = Crypto.DecryptGroup(group.GroupKey, groupId, senderId, actualPayload);
        if (msg == null) return;

        msg.ConversationId = groupId;
        msg.IsMine = false;
        if (_vault.MessageExists(msg.Id)) return;
        _vault.SaveMessage(msg);

        if (groupId == _activeConvId) {
            var senderName = senderContact.DisplayName;
            AddMessageToList(ToViewModel(msg, senderName));
        } else {
            conv.UnreadCount++;
        }

        await Task.CompletedTask;
    }

    // ── Conversation selection ─────────────────────────────────────────────

    void ConvList_SelectionChanged(object s, SelectionChangedEventArgs e) {
        if (ConvList.SelectedItem is not ConvViewModel conv) return;
        OpenConversation(conv);
    }

    void OpenConversation(ConvViewModel conv) {
        _activeConv = conv;
        _activeConvId = conv.Id;
        conv.UnreadCount = 0;

        ActiveConvName.Text = conv.IsGroup
            ? $"group: {conv.DisplayName}"
            : $"dm: {conv.DisplayName} [{conv.ContactData?.UserId[..8]}…]";

        InputBox.IsEnabled = true;
        BtnSend.IsEnabled = true;
        EmptyState.Visibility = Visibility.Collapsed;
        MessageList.Visibility = Visibility.Visible;
        InputBox.Focus();

        // Load initial messages
        LoadInitialMessages(conv.Id);
    }

    // ── Lazy message loading ───────────────────────────────────────────────

    /// <summary>
    /// Load the initial page of messages (newest 50).
    /// This is the only method that replaces the entire list.
    /// All other loads are incremental (prepend older).
    /// </summary>
    void LoadInitialMessages(string convId) {
        _messages.Clear();
        _msgOffset = 0;
        _msgTotal = _vault.GetMessageCount(convId);

        var page = _vault.LoadMessages(convId, 0, 50);
        _msgOffset = page.Count;

        var contacts = _vault.LoadContacts().ToDictionary(c => c.UserId, c => c.DisplayName);
        foreach (var msg in page)
            _messages.Add(ToViewModel(msg, contacts.GetValueOrDefault(msg.SenderId, msg.SenderId[..8])));

        // Scroll to bottom after layout — use Loaded priority to ensure items are rendered
        Dispatcher.InvokeAsync(ScrollToBottom, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Scroll changed handler — triggers history load when user scrolls to top.
    /// Carefully preserves scroll position so the view doesn't jump.
    /// </summary>
    async void OnScrollChanged(object s, ScrollChangedEventArgs e) {
        // Track whether user is near bottom (for auto-scroll on new messages)
        _isNearBottom = _msgScrollViewer != null &&
            _msgScrollViewer.VerticalOffset >= _msgScrollViewer.ScrollableHeight - 80;

        // Trigger history load when near top and there are more messages
        if (e.VerticalOffset < 40 && e.VerticalChange < 0 && !_loadingHistory && _msgOffset < _msgTotal)
            await LoadOlderMessagesAsync();
    }

    async Task LoadOlderMessagesAsync() {
        if (_loadingHistory || _msgOffset >= _msgTotal) return;
        _loadingHistory = true;
        LoadMoreBar.Visibility = Visibility.Visible;

        var convId = _activeConvId;
        var contacts = _vault.LoadContacts().ToDictionary(c => c.UserId, c => c.DisplayName);

        // Load on background thread
        var older = await Task.Run(() => _vault.LoadMessages(convId, _msgOffset, 50));
        _msgOffset += older.Count;

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
                contacts.GetValueOrDefault(msg.SenderId, msg.SenderId[..8])));
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
    }

    /// <summary>
    /// Add a new incoming/sent message to the list.
    /// Only auto-scrolls if user was already near bottom.
    /// </summary>
    void AddMessageToList(MessageViewModel vm) {
        _messages.Add(vm);
        _msgOffset++;
        _msgTotal++;

        if (_isNearBottom)
            Dispatcher.InvokeAsync(ScrollToBottom, DispatcherPriority.Loaded);
    }

    void ScrollToBottom() {
        _msgScrollViewer?.ScrollToEnd();
    }

    // ── Sending messages ──────────────────────────────────────────────────

    void InputBox_KeyDown(object s, KeyEventArgs e) {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) &&
            !Keyboard.IsKeyDown(Key.RightShift)) {
            e.Handled = true;
            _ = SendMessageAsync();
        }
    }

    void InputBox_TextChanged(object s, TextChangedEventArgs e) {
        // Typing indicator (debounced)
        // Could send typing signal to server here — omitted to keep server stateless
    }

    void BtnSend_Click(object s, RoutedEventArgs e) => _ = SendMessageAsync();

    async Task SendMessageAsync() {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _activeConv == null || _user == null) return;
        InputBox.Clear();

        var msg = new Message {
            ConversationId = _activeConvId,
            SenderId = _user.UserId,
            Content = text,
            IsMine = true,
            Status = MessageStatus.Sending,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Show optimistically
        var senderName = $"{_user.DisplayName} (you)";
        var vm = ToViewModel(msg, senderName);
        AddMessageToList(vm);

        if (_activeConv.IsGroup) {
            await SendGroupMessageAsync(msg, vm);
        } else {
            await SendDmAsync(msg, vm);
        }
    }

    async Task SendDmAsync(Message msg, MessageViewModel vm) {
        var contact = _activeConv!.ContactData!;
        if (!await EnsureContactKeysAsync(contact)) {
            vm.Status = MessageStatus.Failed;
            SidebarStatus.Text = $"can't send yet: {contact.DisplayName} hasn't registered keys with this relay";
            return;
        }
        var convKey = GetOrDeriveConvKey(contact);
        if (convKey == null) { vm.Status = MessageStatus.Failed; return; }

        msg.SeqNum = _vault.NextSeqNum(msg.ConversationId);
        var payload = Crypto.EncryptDm(convKey, msg);
        var sig = Crypto.SignPayload(_user!.SignPrivKey, payload, msg.SeqNum);

        _vault.SaveMessage(msg);

        var sent = await _net.SendDmAsync(contact.UserId, payload, sig, msg.SeqNum);
        if (sent) {
            vm.Status = MessageStatus.Sent;
            _vault.UpdateMessageStatus(msg.Id, MessageStatus.Sent);
        } else {
            // Queue in outbox for retry
            vm.Status = MessageStatus.Sending;
            _vault.EnqueueOutbox(msg.Id, contact.UserId, payload, sig, msg.SeqNum);
        }
    }

    async Task SendGroupMessageAsync(Message msg, MessageViewModel vm) {
        var group = _activeConv!.GroupData!;
        msg.SeqNum = _vault.NextSeqNum(msg.ConversationId);
        msg.ConvType = ConversationType.Group;
        var payload = Crypto.EncryptGroup(group.GroupKey, msg);
        var sig = Crypto.SignPayload(_user!.SignPrivKey, payload, msg.SeqNum);

        _vault.SaveMessage(msg);

        var sent = await _net.SendGroupAsync(group.GroupId, group.MemberIds, payload, sig, msg.SeqNum);
        vm.Status = sent ? MessageStatus.Sent : MessageStatus.Sending;
        if (!sent)
            _vault.EnqueueOutbox(msg.Id, group.GroupId, payload, sig, msg.SeqNum,
                ConversationType.Group, group.GroupId, group.MemberIds);
        else
            _vault.UpdateMessageStatus(msg.Id, MessageStatus.Sent);
    }

    // ── Outbox flush (resend failed/offline messages) ──────────────────────

    async Task FlushOutboxAsync() {
        if (!_net.IsConnected) return;
        var pending = _vault.LoadOutbox();
        foreach (var item in pending) {
            bool sent;
            if (item.ConvType == ConversationType.Group && item.MemberIds != null)
                sent = await _net.SendGroupAsync(item.GroupId!, item.MemberIds, item.Payload, item.Sig, item.SeqNum);
            else
                sent = await _net.SendDmAsync(item.RecipientId, item.Payload, item.Sig, item.SeqNum);

            if (sent) {
                _vault.RemoveOutbox(item.Id);
                _vault.UpdateMessageStatus(item.Id, MessageStatus.Sent);
                // Update VM if visible
                var vm = _messages.FirstOrDefault(m => m.Id == item.Id);
                if (vm != null) vm.Status = MessageStatus.Sent;
            } else {
                _vault.IncrementOutboxAttempts(item.Id);
            }
        }
    }

    // ── Key management ────────────────────────────────────────────────────

    async Task<bool> EnsureContactKeysAsync(Contact contact) {
        if (contact.SignPubKey.Length > 0 && contact.DhPubKey.Length > 0) return true;

        var keys = await _net.GetUserKeysAsync(contact.UserId);
        if (keys == null) return false;

        contact.SignPubKey = keys.Value.signPub;
        contact.DhPubKey = keys.Value.dhPub;
        _vault.SaveContact(contact);
        return true;
    }

    byte[]? GetOrDeriveConvKey(Contact contact) {
        var convId = contact.ConversationId ?? contact.UserId;
        if (_convKeys.TryGetValue(convId, out var cached)) return cached;

        // Try loading from vault
        var (_, storedSecret) = _vault.LoadConvState(convId);
        if (storedSecret != null) {
            _convKeys[convId] = storedSecret;
            return storedSecret;
        }

        // Derive from ECDH
        try {
            var secret = Crypto.DeriveSharedSecret(_user!.DhPrivKey, contact.DhPubKey, convId);
            _vault.SaveConvState(convId, 0, secret);
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
        return true;
    }

    static string NormalizeServerUrl(string? serverUrl) {
        var trimmed = serverUrl?.Trim() ?? "";
        return string.IsNullOrEmpty(trimmed) ? DefaultRelayUrl : trimmed.TrimEnd('/');
    }

    static bool IsValidServerUrl(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    static string BuildGroupInviteContent(GroupInfo group) =>
        GroupInvitePrefix + JsonSerializer.Serialize(new GroupInvitePayload(
            group.GroupId,
            group.Name,
            Convert.ToBase64String(group.GroupKey),
            group.MemberIds));

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

    bool TryHandleDirectSystemMessage(Contact senderContact, Message msg) {
        if (!TryParseGroupInvite(msg.Content, out var invite) || invite == null || _user == null)
            return false;

        if (!invite.MemberIds.Contains(_user.UserId) || !invite.MemberIds.Contains(senderContact.UserId))
            return true;

        byte[] groupKey;
        try {
            groupKey = Convert.FromBase64String(invite.GroupKey);
        } catch {
            SidebarStatus.Text = "received an invalid group invite";
            return true;
        }

        var existing = _convs.FirstOrDefault(c => c.GroupData?.GroupId == invite.GroupId);
        var group = existing?.GroupData ?? new GroupInfo {
            GroupId = invite.GroupId,
            CreatedAt = msg.Timestamp
        };

        group.Name = invite.Name;
        group.MemberIds = invite.MemberIds.Distinct().ToList();
        group.GroupKey = groupKey;
        _vault.SaveGroup(group);

        if (existing == null) {
            _convs.Add(new ConvViewModel {
                Id = group.GroupId,
                DisplayName = $"# {group.Name}",
                IsGroup = true,
                GroupData = group
            });
        } else {
            existing.DisplayName = $"# {group.Name}";
            existing.GroupData = group;
        }

        SidebarStatus.Text = $"joined group: {group.Name}";
        return true;
    }

    async Task SendGroupInvitesAsync(GroupInfo group, Dictionary<string, Contact> contactsById) {
        if (_user == null) return;

        foreach (var memberId in group.MemberIds.Where(id => id != _user.UserId)) {
            if (!contactsById.TryGetValue(memberId, out var contact)) continue;
            if (!await EnsureContactKeysAsync(contact)) {
                SidebarStatus.Text = $"group created, but {contact.DisplayName} has not registered relay keys yet";
                continue;
            }

            var convId = contact.ConversationId ?? contact.UserId;
            var convKey = GetOrDeriveConvKey(contact);
            if (convKey == null) {
                SidebarStatus.Text = $"group created, but invite encryption failed for {contact.DisplayName}";
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
                SidebarStatus.Text = $"group invite queued for {contact.DisplayName}";
            }
        }
    }

    // ── Add contact / group ────────────────────────────────────────────────

    void BtnAddContact_Click(object s, RoutedEventArgs e) {
        AddOverlay.Visibility = Visibility.Visible;
        AddContactId.Clear();
        AddContactName.Clear();
        AddStatus.Visibility = Visibility.Collapsed;
        ShowAddDmTab();
    }

    void TabAddDm_Click(object s, RoutedEventArgs e) => ShowAddDmTab();
    void TabAddGroup_Click(object s, RoutedEventArgs e) => ShowAddGroupTab();

    void ShowAddDmTab() {
        AddDmForm.Visibility = Visibility.Visible;
        AddGroupForm.Visibility = Visibility.Collapsed;
        TabAddDm.BorderBrush = (Brush)FindResource("Green");
        TabAddGroup.BorderBrush = (Brush)FindResource("Border");
    }

    void ShowAddGroupTab() {
        AddDmForm.Visibility = Visibility.Collapsed;
        AddGroupForm.Visibility = Visibility.Visible;
        TabAddGroup.BorderBrush = (Brush)FindResource("Green");
        TabAddDm.BorderBrush = (Brush)FindResource("Border");
    }

    void BtnCloseOverlay_Click(object s, RoutedEventArgs e) =>
        AddOverlay.Visibility = Visibility.Collapsed;

    async void BtnConfirmAdd_Click(object s, RoutedEventArgs e) {
        var uid = AddContactId.Text.Trim();
        var name = AddContactName.Text.Trim();

        if (string.IsNullOrEmpty(uid)) { ShowAddStatus("enter a user id"); return; }
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
            ContactData = contact
        };
        _convs.Add(conv);
        AddOverlay.Visibility = Visibility.Collapsed;

        // Open new conversation
        ConvList.SelectedItem = conv;
    }

    async void BtnCreateGroup_Click(object s, RoutedEventArgs e) {
        var name = NewGroupName.Text.Trim();
        if (string.IsNullOrEmpty(name)) { ShowAddStatus("enter a group name"); return; }

        var memberLines = GroupMemberIds.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var memberIds = memberLines.Select(m => m.Trim())
                                   .Where(m => !string.IsNullOrEmpty(m) && m != _user!.UserId)
                                   .Distinct().ToList();
        if (memberIds.Count == 0) { ShowAddStatus("add at least one other member"); return; }

        var contactsById = _vault.LoadContacts().ToDictionary(c => c.UserId);
        var missingContacts = memberIds.Where(id => !contactsById.ContainsKey(id)).ToList();
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
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _vault.SaveGroup(group);
        var conv = new ConvViewModel {
            Id = group.GroupId,
            DisplayName = $"# {name}",
            IsGroup = true,
            GroupData = group
        };
        _convs.Add(conv);
        AddOverlay.Visibility = Visibility.Collapsed;
        ConvList.SelectedItem = conv;
        await SendGroupInvitesAsync(group, contactsById);
    }

    void ShowAddStatus(string msg, bool isError = true) {
        AddStatus.Text = msg;
        AddStatus.Foreground = isError ? (Brush)FindResource("Red") : (Brush)FindResource("Dim");
        AddStatus.Visibility = Visibility.Visible;
    }

    // ── My ID copy ────────────────────────────────────────────────────────

    void BtnMyId_Click(object s, RoutedEventArgs e) {
        if (_user == null) return;
        Clipboard.SetText(_user.UserId);
        var prev = BtnMyId.Content;
        BtnMyId.Content = "[copied!]";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { BtnMyId.Content = prev; t.Stop(); };
        t.Start();
    }

    // ── NUKE ──────────────────────────────────────────────────────────────

    void BtnNuke_Click(object s, RoutedEventArgs e) {
        NukeConfirm.Clear();
        NukeOverlay.Visibility = Visibility.Visible;
        NukeConfirm.Focus();
    }

    void BtnCancelNuke_Click(object s, RoutedEventArgs e) =>
        NukeOverlay.Visibility = Visibility.Collapsed;

    async void BtnConfirmNuke_Click(object s, RoutedEventArgs e) {
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
                } catch { }
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
        Application.Current.Shutdown();
    }

    void BtnConfirmNuke_Click_inner() {
        IsEnabled = false; // Prevent any further interaction
    }

    // ── Helper: ViewModel factory ─────────────────────────────────────────

    MessageViewModel ToViewModel(Message msg, string senderName) => new() {
        Id = msg.Id,
        ConversationId = msg.ConversationId,
        SenderId = msg.SenderId,
        SenderLabel = msg.IsMine ? $"{_user?.DisplayName ?? "me"}:" : $"{senderName}:",
        Content = msg.Content,
        Timestamp = msg.Timestamp,
        SeqNum = msg.SeqNum,
        IsMine = msg.IsMine,
        Status = msg.Status
    };

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

    protected override async void OnClosed(EventArgs e) {
        _outboxTimer?.Stop();
        await _net.DisposeAsync();
        foreach (var key in _convKeys.Values) Crypto.Wipe(key);
        _vault.Dispose();
        base.OnClosed(e);
    }
}
