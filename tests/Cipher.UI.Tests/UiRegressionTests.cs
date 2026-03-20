namespace Cipher.UI.Tests;

public class UiRegressionTests {
    const string TestPassword = "cipherpass123";

    [Fact]
    public async Task FirstLaunchShowsRegisterFormWithRuntimeRelayUrl() {
        var relayUrl = $"http://127.0.0.1:{LocalRelayProcess.GetFreePort()}";
        var appDataDir = UiTestPaths.CreateTempDirectory("first-launch");

        await using var app = await UiAppSession.LaunchAsync(appDataDir, relayUrl);
        await app.WaitForSignalAsync("auth-ready", detailPredicate: detail => detail == "register");

        Assert.Equal(relayUrl, app.ReadValue("RegisterServerUrl"));

        app.Click("TabLogin");

        Assert.Equal(relayUrl, app.ReadValue("LoginServerUrl"));
    }

    [Fact]
    public async Task AppRecoversWhenRelayStartsAfterClientRegistration() {
        using var portReservation = PortReservation.Reserve();
        var relayUrl = $"http://127.0.0.1:{portReservation.Port}";
        var appDataDir = UiTestPaths.CreateTempDirectory("late-relay");

        await using var app = await UiAppSession.LaunchAsync(
            appDataDir,
            relayUrl,
            "--test-auto-register",
            "--test-register-name=LateRelayUser",
            $"--test-register-password={TestPassword}");
        await app.WaitForSignalAsync("auth-ready", detailPredicate: detail => detail == "register");
        await app.WaitForSignalAsync("chat-ready");

        portReservation.Dispose();

        await using var relay = new LocalRelayProcess(portReservation.Port);
        await relay.StartAsync();

        await app.WaitForTextAsync("ConnStatusText", text => text == "online", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TwoClientsExchangeDirectMessageWithoutDuplicateRows() {
        var port = LocalRelayProcess.GetFreePort();
        await using var relay = new LocalRelayProcess(port);
        await relay.StartAsync();

        var aliceDir = UiTestPaths.CreateTempDirectory("alice");
        var bobDir = UiTestPaths.CreateTempDirectory("bob");

        await using var alice = await UiAppSession.LaunchAsync(
            aliceDir,
            relay.BaseUrl,
            "--test-auto-register",
            "--test-register-name=Alice",
            $"--test-register-password={TestPassword}");
        await using var bob = await UiAppSession.LaunchAsync(
            bobDir,
            relay.BaseUrl,
            "--test-auto-register",
            "--test-register-name=Bob",
            $"--test-register-password={TestPassword}");

        await alice.WaitForSignalAsync("auth-ready", detailPredicate: detail => detail == "register");
        await bob.WaitForSignalAsync("auth-ready", detailPredicate: detail => detail == "register");

        await alice.WaitForSignalAsync("chat-ready");
        await bob.WaitForSignalAsync("chat-ready");
        await alice.WaitForTextAsync("ConnStatusText", text => text == "online", TimeSpan.FromSeconds(20));
        await bob.WaitForTextAsync("ConnStatusText", text => text == "online", TimeSpan.FromSeconds(20));

        var aliceId = alice.ReadHelpText("BtnMyId");
        var bobId = bob.ReadHelpText("BtnMyId");
        Assert.False(string.IsNullOrWhiteSpace(aliceId));
        Assert.False(string.IsNullOrWhiteSpace(bobId));

        AddDirectContact(alice, bobId, "Bob");
        AddDirectContact(bob, aliceId, "Alice");

        alice.SetText("InputBox", "hello bob from ui");
        alice.Click("BtnSend");

        await WaitForConditionAsync(
            () => bob.HasText("hello bob from ui"),
            TimeSpan.FromSeconds(20),
            "Bob did not receive the direct message");

        Assert.Equal(1, bob.CountTextOccurrences("hello bob from ui"));

        await Task.Delay(1500);
        Assert.Equal(1, bob.CountTextOccurrences("hello bob from ui"));
    }

    [Fact]
    public async Task SettingsOverlayShowsVersionAndDiagnostics() {
        var port = LocalRelayProcess.GetFreePort();
        await using var relay = new LocalRelayProcess(port);
        await relay.StartAsync();

        var appDataDir = UiTestPaths.CreateTempDirectory("settings");
        await using var app = await UiAppSession.LaunchAsync(
            appDataDir,
            relay.BaseUrl,
            "--test-auto-register",
            "--test-register-name=SettingsUser",
            $"--test-register-password={TestPassword}");
        await app.WaitForSignalAsync("auth-ready", detailPredicate: detail => detail == "register");
        await app.WaitForSignalAsync("chat-ready");
        await app.WaitForTextAsync("ConnStatusText", text => text == "online", TimeSpan.FromSeconds(20));

        app.Click("BtnSettings");

        await app.WaitForTextAsync("SettingsVersionText", text => !string.IsNullOrWhiteSpace(text), TimeSpan.FromSeconds(10));
        await app.WaitForTextAsync("SettingsDiagnosticsText", text => text.Contains("data:", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10));
    }
    static void AddDirectContact(UiAppSession app, string userId, string displayName) {
        app.Click("BtnSidebarAddFriend");
        app.SetText("AddContactId", userId);
        app.SetText("AddContactName", displayName);
        app.Click("BtnConfirmAddContact");
    }

    static async Task WaitForConditionAsync(Func<bool> check, TimeSpan timeout, string message) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            if (check()) {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(message);
    }
}
