# Codex working preferences

- Always run PowerShell commands without loading PowerShell profiles. Set the shell tool's `login` option to `false`; when launching `powershell.exe` or `pwsh.exe` explicitly, pass `-NoProfile`.
- Always disable .NET CLI telemetry. Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` in the command environment before every `dotnet` invocation.
- Modern Language Features: Use modern C# features. Prefer primary constructors, collection expressions ([]), switch expressions, and pattern matching over legacy syntax.
- Asynchronous Programming: Always write asynchronous code using async/await. Avoid blocking calls like .Result or .Wait(). Always propagate CancellationToken parameters where applicable.
- Null Safety: Enforce strict Nullable Reference Types (NRT). Ensure all parameters are validated for null using ArgumentNullException.ThrowIfNull() where appropriate.
- Performance Best Practices: Use ReadOnlySpan<T> and Memory<T> for high-performance memory-sensitive tasks. Prefer StringBuilder for complex string concatenations inside loops.
- Dependency Injection: Design classes to support native .NET dependency injection (DI). Avoid the Service Locator pattern. Always use constructor injection.
- Error Handling: Avoid empty catch blocks. Throw specific, meaningful exceptions. Use global exception handling middleware for API boundaries instead of wrapping every method in broad try-catch blocks.
- LINQ Style: Write LINQ queries using method syntax (e.g., .Where().Select()) rather than query syntax, unless the query is exceptionally complex.