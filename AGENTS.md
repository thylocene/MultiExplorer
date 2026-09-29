# Codex working preferences

- Always run PowerShell commands without loading PowerShell profiles. Set the shell tool's `login` option to `false`; when launching `powershell.exe` or `pwsh.exe` explicitly, pass `-NoProfile`.
- Always disable .NET CLI telemetry. Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` in the command environment before every `dotnet` invocation.
- Always provide the full absolute path for every output artifact delivered to the user, as plain text on its own line. The full path must be visible in the response; never hide it behind a Markdown link or filename-only label. Apply this in every new session for this workspace.
- After implementing changes, always build and provide the Debug version first so the user can test it. Do not build an installer package unless the user explicitly requests one after testing.
- Modern Language Features: Use modern C# features. Prefer primary constructors, collection expressions ([]), switch expressions, and pattern matching over legacy syntax.
- Asynchronous Programming: Always write asynchronous code using async/await. Avoid blocking calls like .Result or .Wait(). Always propagate CancellationToken parameters where applicable.
- Null Safety: Enforce strict Nullable Reference Types (NRT). Ensure all parameters are validated for null using ArgumentNullException.ThrowIfNull() where appropriate.
- Performance Best Practices: Use ReadOnlySpan<T> and Memory<T> for high-performance memory-sensitive tasks. Prefer StringBuilder for complex string concatenations inside loops.
- Dependency Injection: Design classes to support native .NET dependency injection (DI). Avoid the Service Locator pattern. Always use constructor injection.
- Error Handling: Avoid empty catch blocks. Throw specific, meaningful exceptions. Use global exception handling middleware for API boundaries instead of wrapping every method in broad try-catch blocks.
- LINQ Style: Write LINQ queries using method syntax (e.g., .Where().Select()) rather than query syntax, unless the query is exceptionally complex.
- DPI-aware UI: Every new WinForms form and its child controls must remain readable and fully visible at 100%, 150%, 200%, and 250% scaling and when moved between monitors with different DPI. Set `AutoScaleDimensions` to 96 x 96 and `AutoScaleMode` to `Dpi`, use responsive layouts, and scale sizes held outside the control tree (such as grid row heights) on DPI changes.
- Rounded buttons: Use the project's `RoundedButton` control for every button on new forms and dialogs. Size buttons so their text remains visible at the supported DPI scales.
