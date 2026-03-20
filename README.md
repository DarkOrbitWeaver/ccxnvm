# Cipher Client

## Live relay

The desktop client defaults to:

`https://cipher-relay.onrender.com`

When the relay is cold-starting or briefly unavailable, the client now keeps retrying in the background and queued outbox messages flush automatically after reconnect.

Direct-contact security improvements:

- manual safety-number verification for direct contacts
- key-change review that blocks direct sending until you approve the new keys
- ciphertext padding so the relay learns less from exact message length

## Relay storage

The relay now supports two storage backends:

- `sqlite` for local development and smoke tests
- `turso` for free hosted durable storage

The server auto-selects `turso` when both of these environment variables are present:

- `TURSO_DATABASE_URL`
- `TURSO_AUTH_TOKEN`

Optional relay environment variables:

- `RELAY_STORE=sqlite|turso`
- `RELAY_SQLITE_PATH=/path/to/relay.db`
- `RELAY_PENDING_TTL_DAYS=30`
- `RELAY_MAX_PENDING_PER_RECIPIENT=200`

For a free durable relay on Render, set:

```text
RELAY_STORE=turso
TURSO_DATABASE_URL=libsql://<db>-<org>.turso.io
TURSO_AUTH_TOKEN=<token>
```

Quick setup flow:

1. Create a free Turso database.
2. Create a Turso auth token for that database.
3. In Render, add `RELAY_STORE=turso`, `TURSO_DATABASE_URL`, and `TURSO_AUTH_TOKEN` to the relay service environment.
4. Redeploy the Render service once.
5. Confirm `GET /health` now reports `"storage":"turso"`.

Only relay metadata is stored server-side:

- public keys
- ciphertext payloads
- signatures
- recipient IDs
- sequence numbers
- timestamps
- delivery ack state

## Branding

Edit `Branding.props` to change:

- executable name
- product/app name
- company
- description

Icons already wired:

- `Assets/256x256.ico` for the app/exe icon
- `Assets/48x48.ico` for the window/taskbar icon

The app title bar and local `%APPDATA%` storage folder follow the configured product name automatically.

## Session security

The login/register screens now let you choose whether to remember the vault key on this device.

- checked: faster relaunches via Windows DPAPI
- unchecked: stronger local security because the password is required again next launch

## Friend build

Run:

```powershell
.\publish-friend.ps1
```

The exported self-contained build will be written to `.publish\win-x64`.

The script also creates:

- `.publish\Cipher-win-x64.zip` for easier sharing
- `.publish\win-x64\README.txt` with run + data-location notes for your friend

This portable build is still useful for quick sharing, but it does not include installer-based auto-updates. Your friend can unzip the package and run the `.exe` from any normal folder. The encrypted vault and session files are stored separately in `%APPDATA%\Cipher`, not beside the executable.

## Release packaging

For the real release flow with installer + in-app updates, run:

```powershell
.\publish-release.ps1
```

That produces:

- `.publish\win-x64-release` with the unpacked release build
- `.releases\stable` with the Velopack installer, packages, and update metadata

Upload the contents of `.releases\stable` to a GitHub Release on this repo. Once your friend installs that packaged build, the app can check GitHub Releases in the background and apply downloaded updates after a manual restart.

## Diagnostics and recovery

The app now keeps local operational data under `%APPDATA%\Cipher\`:

- `vault.db`, `vault.salt`, `session.bin`
- `logs\` for rolling diagnostics logs
- `backups\` for automatic pre-repair / pre-migration / pre-update vault backups
- `exports\` for diagnostics bundles created from the in-app system panel

Additional hardening now in the desktop client:

- startup health checks for writable app-data, session readability, default relay URL, and vault `quick_check`
- rolling local logs for startup, reconnects, updater activity, and unexpected exceptions
- automatic vault backup before schema repair, migration, or update handoff
- repair of invalid outbox rows and missing conversation state on open
- bounded outbox flush so reconnect retries do not overlap
- system panel in the client for version, update status, logs, data folder, and diagnostics export

## Important limitations

- The Render free relay can sleep after inactivity, so first reconnects can be slow.
- Render free still sleeps, but offline recipient delivery is now durable when Turso is configured.
- The relay remains zero-trust for message contents, but it still is not hardened for hostile internet-scale abuse.
- This is suitable for trusted hobby use, but it is not a production-scale hardened messaging service.
