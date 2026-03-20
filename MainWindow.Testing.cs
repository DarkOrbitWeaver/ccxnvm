using System.IO;
using System.Windows;
using System.Windows.Automation;

namespace Cipher;

public partial class MainWindow {
    const string DefaultTestPassword = "cipherpass123";

    void InitializeTestingFeatures() {
        ApplyAutomationIds();
        Loaded += (_, _) => AppRuntime.WriteSignal("window-ready", AuthPanel.Visibility == Visibility.Visible ? "auth" : "chat");
    }

    void ApplyAutomationIds() {
        SetAutomationId(this, "CipherWindow");
        SetAutomationId(AuthPanel, "AuthPanel");
        SetAutomationId(AuthBrandTitle, "AuthBrandTitle");
        SetAutomationId(AuthStatus, "AuthStatus");
        SetAutomationId(TabLogin, "TabLogin");
        SetAutomationId(TabRegister, "TabRegister");
        SetAutomationId(LoginForm, "LoginForm");
        SetAutomationId(LoginPass, "LoginPassword");
        SetAutomationId(LoginServerUrl, "LoginServerUrl");
        SetAutomationId(LoginRememberSession, "LoginRememberSession");
        SetAutomationId(BtnLogin, "BtnLogin");
        SetAutomationId(RegisterForm, "RegisterForm");
        SetAutomationId(RegName, "RegisterName");
        SetAutomationId(RegPass, "RegisterPassword");
        SetAutomationId(RegPass2, "RegisterPasswordConfirm");
        SetAutomationId(RegServerUrl, "RegisterServerUrl");
        SetAutomationId(RegisterRememberSession, "RegisterRememberSession");
        SetAutomationId(BtnRegister, "BtnRegister");
        SetAutomationId(VaultStorageHintText, "VaultStorageHint");

        SetAutomationId(ChatPanel, "ChatPanel");
        SetAutomationId(ConnDot, "ConnDot");
        SetAutomationId(ConnStatusText, "ConnStatusText");
        SetAutomationId(ActiveConvName, "ActiveConversation");
        SetAutomationId(BtnSecurityReview, "BtnSecurityReview");
        SetAutomationId(BtnMyId, "BtnMyId");
        SetAutomationId(BtnOpenAddOverlay, "BtnOpenAddOverlay");
        SetAutomationId(BtnSidebarAddFriend, "BtnSidebarAddFriend");
        SetAutomationId(BtnGroupInvite, "BtnGroupInvite");
        SetAutomationId(BtnGroupMenu, "BtnGroupMenu");
        SetAutomationId(BtnQuickNewGroup, "BtnQuickNewGroup");
        SetAutomationId(BtnOpenNukeOverlay, "BtnOpenNukeOverlay");
        SetAutomationId(BtnSettings, "BtnSettings");
        SetAutomationId(ConvList, "ConversationList");
        SetAutomationId(EmptyState, "EmptyState");
        SetAutomationId(MessageList, "MessageList");
        SetAutomationId(LoadMoreBar, "LoadMoreBar");
        SetAutomationId(TypingIndicator, "TypingIndicator");
        SetAutomationId(BtnEmojiPicker, "BtnEmojiPicker");
        SetAutomationId(EmojiPopup, "EmojiPopup");
        SetAutomationId(EmojiPanel, "EmojiPanel");
        SetAutomationId(InputBox, "InputBox");
        SetAutomationId(BtnSend, "BtnSend");

        SetAutomationId(AddOverlay, "AddOverlay");
        SetAutomationId(TabAddDm, "TabAddDirect");
        SetAutomationId(TabAddGroup, "TabAddGroup");
        SetAutomationId(AddStatusPanel, "AddStatusPanel");
        SetAutomationId(AddStatus, "AddStatus");
        SetAutomationId(AddDmForm, "AddDirectForm");
        SetAutomationId(AddContactId, "AddContactId");
        SetAutomationId(AddContactName, "AddContactName");
        SetAutomationId(BtnConfirmAddContact, "BtnConfirmAddContact");
        SetAutomationId(BtnCloseAddOverlay, "BtnCloseAddOverlay");
        SetAutomationId(AddGroupForm, "AddGroupForm");
        SetAutomationId(GroupModeHint, "GroupModeHint");
        SetAutomationId(NewGroupName, "NewGroupName");
        SetAutomationId(GroupMemberIds, "GroupMemberIds");
        SetAutomationId(BtnConfirmCreateGroup, "BtnConfirmCreateGroup");
        SetAutomationId(BtnCloseGroupOverlay, "BtnCloseGroupOverlay");
        SetAutomationId(GroupMenuPopup, "GroupMenuPopup");
        SetAutomationId(GroupMenuTitleText, "GroupMenuTitleText");
        SetAutomationId(GroupMenuOwnerText, "GroupMenuOwnerText");
        SetAutomationId(GroupMenuMembersText, "GroupMenuMembersText");
        SetAutomationId(BtnGroupMenuInvite, "BtnGroupMenuInvite");
        SetAutomationId(BtnGroupMenuLeave, "BtnGroupMenuLeave");
        SetAutomationId(BtnGroupMenuDelete, "BtnGroupMenuDelete");

        SetAutomationId(SettingsOverlay, "SettingsOverlay");
        SetAutomationId(ChkCloseToTrayOnClose, "ChkCloseToTrayOnClose");
        SetAutomationId(ChkStartWithWindows, "ChkStartWithWindows");
        SetAutomationId(ChkStartHiddenOnStartup, "ChkStartHiddenOnStartup");
        SetAutomationId(BtnStartupDelay0, "BtnStartupDelay0");
        SetAutomationId(BtnStartupDelay15, "BtnStartupDelay15");
        SetAutomationId(BtnStartupDelay30, "BtnStartupDelay30");
        SetAutomationId(BtnStartupDelay60, "BtnStartupDelay60");
        SetAutomationId(SettingsVersionText, "SettingsVersionText");
        SetAutomationId(SettingsUpdateStatusText, "SettingsUpdateStatusText");
        SetAutomationId(UpdateProgressBar, "UpdateProgressBar");
        SetAutomationId(SettingsDiagnosticsText, "SettingsDiagnosticsText");
        SetAutomationId(BtnCheckUpdates, "BtnCheckUpdates");
        SetAutomationId(BtnRestartToUpdate, "BtnRestartToUpdate");
        SetAutomationId(BtnOpenLogs, "BtnOpenLogs");
        SetAutomationId(BtnOpenData, "BtnOpenData");
        SetAutomationId(BtnExportDiagnostics, "BtnExportDiagnostics");

        SetAutomationId(NukeOverlay, "NukeOverlay");
        SetAutomationId(NukeConfirm, "NukeConfirm");
        SetAutomationId(BtnConfirmNuke, "BtnConfirmNuke");
        SetAutomationId(BtnCancelNuke, "BtnCancelNuke");
    }

    void SignalAuthReady(string mode) =>
        AppRuntime.WriteSignal("auth-ready", mode);

    void SignalChatReady() =>
        AppRuntime.WriteSignal("chat-ready", _user?.UserId);

    void SignalRelayState(RelayConnectionState state, string? detail) =>
        AppRuntime.WriteSignal("relay-state", $"{state}:{detail}");

    void UpdateAutomationIdentity() {
        if (_user == null) return;
        AutomationProperties.SetHelpText(BtnMyId, _user.UserId);
        AutomationProperties.SetItemStatus(BtnMyId, _user.DisplayName);
    }

    static void SetAutomationId(DependencyObject target, string automationId) =>
        AutomationProperties.SetAutomationId(target, automationId);

    async Task RunTestStartupActionsAsync() {
        if (!AppRuntime.IsTestMode) return;

        if (AppRuntime.Current.AutoRegister &&
            RegisterForm.Visibility == Visibility.Visible &&
            !File.Exists(Vault.DefaultVaultPath)) {
            RegName.Text = string.IsNullOrWhiteSpace(AppRuntime.Current.TestRegisterName)
                ? "TestUser"
                : AppRuntime.Current.TestRegisterName;

            var password = string.IsNullOrEmpty(AppRuntime.Current.TestRegisterPassword)
                ? DefaultTestPassword
                : AppRuntime.Current.TestRegisterPassword;

            RegPass.Password = password;
            RegPass2.Password = password;
            RegisterRememberSession.IsChecked = false;
            AppRuntime.WriteSignal("auth-auto-register", "start");
            await RegisterAsync();
            return;
        }

        if (AppRuntime.Current.AutoLogin &&
            LoginForm.Visibility == Visibility.Visible &&
            File.Exists(Vault.DefaultVaultPath)) {
            LoginPass.Password = string.IsNullOrEmpty(AppRuntime.Current.TestRegisterPassword)
                ? DefaultTestPassword
                : AppRuntime.Current.TestRegisterPassword;
            LoginRememberSession.IsChecked = false;
            AppRuntime.WriteSignal("auth-auto-login", "start");
            await LoginAsync();
        }
    }
}
