# Cipher Client

## Live relay

The desktop client defaults to:

`https://cipher-relay.onrender.com`

## Branding

Edit `Branding.props` to change:

- executable name
- product/app name
- company
- description

Optional icon:

- add `Assets\AppIcon.ico`

The app title bar and local `%APPDATA%` storage folder follow the configured product name automatically.

## Friend build

Run:

```powershell
.\publish-friend.ps1
```

The exported self-contained build will be written to `.publish\win-x64`.

## Important limitations

- The Render free relay can sleep after inactivity, so first reconnects can be slow.
- Relay state is in memory only; server restarts can drop queued offline messages and cached public keys.
- This is suitable for testing and small trusted use, but it is not hardened for hostile internet-scale abuse.
