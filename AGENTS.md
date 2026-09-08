# Codex working preferences

- Always run PowerShell commands without loading PowerShell profiles. Set the shell tool's `login` option to `false`; when launching `powershell.exe` or `pwsh.exe` explicitly, pass `-NoProfile`.
- Always disable .NET CLI telemetry. Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` in the command environment before every `dotnet` invocation.