# Contributing

## Development setup

- Windows 10 or later
- .NET 8 SDK
- System OpenSSH (`ssh.exe`)

Restore, build, and test with the project paths below:

```powershell
dotnet restore SSHTunnelManager.csproj
dotnet build SSHTunnelManager.csproj -c Release
dotnet test Tests\SSHTunnelManager.Tests.csproj -c Release
```

Keep the application tray-only and preserve the read-only SSH config model. Do not commit private keys, passwords, personal host names, settings files, or build output. Changes that affect tunnel detection should include parser or manager tests.

## Pull requests

Describe the user-visible behavior, the config syntax involved, and the validation you ran. Keep changes focused and avoid adding dependencies unless they are needed for the feature.
