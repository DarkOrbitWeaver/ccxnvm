using Cipher;

if (args.Length < 2) {
    Console.Error.WriteLine("usage: Cipher.ReconnectProbe <wait-connect|wait-reconnect> <serverUrl>");
    return 2;
}

var mode = args[0];
var serverUrl = args[1];

var exitCode = mode switch {
    "wait-connect" => await WaitForConnectAsync(serverUrl),
    "wait-reconnect" => await WaitForReconnectAsync(serverUrl),
    _ => 2
};

return exitCode;

static async Task<int> WaitForConnectAsync(string serverUrl) {
    await using var net = new NetworkClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    net.OnStateChanged += (state, detail) => Console.WriteLine($"STATE {state} {detail}");
    net.OnConnected += () => connected.TrySetResult();
    net.OnError += msg => Console.WriteLine($"ERROR {msg}");

    await net.ConnectAsync(CreateUser(serverUrl, "connect-probe"));

    try {
        await connected.Task.WaitAsync(timeout.Token);
        Console.WriteLine("CONNECTED");
        return 0;
    } catch (OperationCanceledException) {
        Console.Error.WriteLine("Timed out waiting for initial connection.");
        return 1;
    }
}

static async Task<int> WaitForReconnectAsync(string serverUrl) {
    await using var net = new NetworkClient();
    using var initialTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var initialConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sawDisconnect = false;

    net.OnStateChanged += (state, detail) => Console.WriteLine($"STATE {state} {detail}");
    net.OnConnected += () => {
        if (!initialConnected.Task.IsCompleted) {
            initialConnected.TrySetResult();
            return;
        }

        if (sawDisconnect) {
            reconnected.TrySetResult();
        }
    };
    net.OnDisconnected += () => {
        if (initialConnected.Task.IsCompleted) {
            sawDisconnect = true;
            Console.WriteLine("DISCONNECTED");
        }
    };
    net.OnError += msg => Console.WriteLine($"ERROR {msg}");

    await net.ConnectAsync(CreateUser(serverUrl, "reconnect-probe"));

    try {
        await initialConnected.Task.WaitAsync(initialTimeout.Token);
        Console.WriteLine("INITIAL_CONNECTED");
    } catch (OperationCanceledException) {
        Console.Error.WriteLine("Timed out waiting for the initial connection.");
        return 1;
    }

    try {
        await reconnected.Task.WaitAsync(reconnectTimeout.Token);
        Console.WriteLine("RECONNECTED");
        return 0;
    } catch (OperationCanceledException) {
        Console.Error.WriteLine("Timed out waiting for reconnect after relay restart.");
        return 1;
    }
}

static LocalUser CreateUser(string serverUrl, string displayName) {
    var (signPriv, signPub) = Crypto.GenerateSigningKeys();
    var (dhPriv, dhPub) = Crypto.GenerateDhKeys();

    return new LocalUser {
        UserId = Crypto.DeriveUserId(signPub),
        DisplayName = displayName,
        SignPrivKey = signPriv,
        SignPubKey = signPub,
        DhPrivKey = dhPriv,
        DhPubKey = dhPub,
        ServerUrl = serverUrl,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}
