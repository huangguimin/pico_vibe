using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PicoVibe.Hook;

/// <summary>
/// Installs user-level Cursor, Codex and Claude Code hooks without replacing unrelated
/// configuration. PICO_VIBE_TEST_HOME is intentionally supported so the full
/// merge/migration flow can be tested without touching the real user profile.
/// </summary>
internal static class HookInstaller
{
    internal const string TestHomeEnvironmentVariable = "PICO_VIBE_TEST_HOME";
    internal const string TestFailurePointEnvironmentVariable = "PICO_VIBE_HOOK_TEST_FAILURE_POINT";
    internal const string TestFailureAfterCursorUpdate = "AfterCursorUpdate";
    private const string SkillMarkerFile = ".pico-vibe-managed";
    private const string HookFileMarkerSuffix = ".pico-vibe-created";

    private static readonly (string Event, string Args, int Timeout)[] CursorEvents =
    {
        ("sessionStart", "hook idle --event sessionStart", 5),
        ("beforeSubmitPrompt", "hook thinking --event beforeSubmitPrompt", 5),
        ("preToolUse", "hook tools --event preToolUse", 605),
        ("beforeShellExecution", "hook ask --event beforeShellExecution", 605),
        ("beforeMCPExecution", "hook ask --event beforeMCPExecution", 605),
        ("postToolUse", "hook thinking --event postToolUse", 5),
        ("postToolUseFailure", "hook error --event postToolUseFailure", 5),
        ("stop", "hook done --event stop", 5),
        ("sessionEnd", "hook idle --event sessionEnd", 5),
    };

    // Claude Code 2.x has a native blocking PermissionRequest boundary.  The
    // obsolete Notification entry remains in this migration table only so a
    // reinstall/uninstall removes Pico Vibe's old handler; it is never added.
    // Tool events take a matcher; lifecycle events must not have one.
    private static readonly (string Event, int Timeout, string? Matcher, bool Install)[] ClaudeEvents =
    {
        ("SessionStart", 5, null, true),
        ("SessionEnd", 3, null, true),
        ("UserPromptSubmit", 5, null, true),
        ("PreToolUse", 5, "*", true),
        ("PermissionRequest", 605, "*", true),
        ("PostToolUse", 5, "*", true),
        ("PostToolUseFailure", 5, "*", true),
        ("Notification", 5, null, false),
        ("Stop", 5, null, true),
        ("SubagentStart", 5, "*", true),
        ("SubagentStop", 5, "*", true),
    };

    // Codex limits SessionEnd to three seconds. PermissionRequest remains
    // synchronous so a physical touch can return a native allow/deny decision.
    private static readonly (string Event, int Timeout)[] CodexEvents =
    {
        ("SessionStart", 5),
        ("SessionEnd", 3),
        ("UserPromptSubmit", 5),
        ("PreToolUse", 5),
        ("PermissionRequest", 605),
        ("PostToolUse", 5),
        ("Stop", 5),
    };

    public static int Install(string? skillSource) =>
        Install(skillSource, executableOverride: null);

    internal static int Install(string? skillSource, string? executableOverride)
    {
        var home = UserHome();
        using var installLock = AcquireInstallLock(home);
        var executable = ResolveExecutable(executableOverride);
        ValidateExistingHookFiles(home);
        var validatedSkillSource = ValidateSkillSource(home, skillSource);

        UpdateCursor(home, executable, uninstall: false);
        ThrowIfIsolatedTestFailure(TestFailureAfterCursorUpdate);
        UpdateCodex(home, executable, uninstall: false);
        UpdateClaude(home, executable, uninstall: false);
        if (validatedSkillSource is not null)
        {
            InstallSkill(home, validatedSkillSource);
        }
        DeleteLegacyScripts(home);

        Console.WriteLine("Pico Vibe Cursor/Codex/Claude hooks installed; unrelated hooks were preserved.");
        Console.WriteLine("Open /hooks once in Codex to review and trust PicoVibeHook.");
        return 0;
    }

    public static int Uninstall()
    {
        var home = UserHome();
        using var installLock = AcquireInstallLock(home);
        ValidateExistingHookFiles(home);

        UpdateCursor(home, executable: null, uninstall: true);
        UpdateCodex(home, executable: null, uninstall: true);
        UpdateClaude(home, executable: null, uninstall: true);
        DeleteLegacyScripts(home);
        DeleteManagedSkill(home);

        Console.WriteLine("Pico Vibe hooks removed; unrelated hooks were preserved.");
        return 0;
    }

    private static void UpdateCursor(string home, string? executable, bool uninstall)
    {
        var path = SafeUserPath(home, ".cursor", "hooks.json");
        var marker = path + HookFileMarkerSuffix;
        var existed = File.Exists(path);
        var root = LoadObject(path, create: !uninstall);
        if (root is null)
        {
            DeleteFileIfPresent(marker);
            return;
        }

        var changed = false;
        var hooks = GetHooksObject(root, path, create: !uninstall, ref changed);
        if (hooks is null)
        {
            return;
        }

        if (!uninstall && root["version"] is null)
        {
            root["version"] = 1;
            changed = true;
        }

        foreach (var (eventName, arguments, timeout) in CursorEvents)
        {
            var entries = GetEventArray(hooks, eventName, create: !uninstall, ref changed);
            if (entries is null)
            {
                continue;
            }

            changed |= RemoveCursorOwnedEntries(entries);
            if (!uninstall)
            {
                var entry = new JsonObject
                {
                    ["command"] = BuildCommand(executable!, arguments),
                    ["timeout"] = timeout,
                    // Status lighting and optional touch control must never turn
                    // a hook process failure into an implicit policy block.
                    ["failClosed"] = false,
                };
                if (eventName == "stop")
                {
                    entry["loop_limit"] = 5;
                }
                entries.Add(entry);
                changed = true;
            }

            if (entries.Count == 0)
            {
                hooks.Remove(eventName);
                changed = true;
            }
        }

        if (uninstall)
        {
            PruneInstallerOnlyDocument(root, isCursor: true, path, marker, ref changed);
        }
        else if (changed)
        {
            WriteObject(path, root);
            if (!existed)
            {
                WriteOwnershipMarker(marker);
            }
        }
    }

    // Claude Code keeps hooks in settings.json, which also holds permissions,
    // model choice and everything else the user configured.  So this never
    // prunes the document down to "installer-only" the way the Cursor and Codex
    // paths may: it only removes its own handlers, and only deletes the file if
    // the installer created it and nothing else was ever added.
    private static void UpdateClaude(string home, string? executable, bool uninstall)
    {
        var path = SafeUserPath(home, ".claude", "settings.json");
        var marker = path + HookFileMarkerSuffix;
        var existed = File.Exists(path);
        var root = LoadObject(path, create: !uninstall);
        if (root is null)
        {
            DeleteFileIfPresent(marker);
            return;
        }

        var changed = false;
        var hooks = GetHooksObject(root, path, create: !uninstall, ref changed);
        if (hooks is null)
        {
            return;
        }

        foreach (var (eventName, timeout, matcher, install) in ClaudeEvents)
        {
            var entries = GetEventArray(hooks, eventName, create: !uninstall, ref changed);
            if (entries is null)
            {
                continue;
            }

            // Same document shape as Codex: event -> groups -> hooks.
            changed |= RemoveCodexOwnedHandlers(entries);
            if (!uninstall && install)
            {
                var handler = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = BuildCommand(executable!, $"claude-hook --event {eventName}"),
                    ["timeout"] = timeout,
                };
                var entry = new JsonObject();
                if (matcher is not null)
                {
                    // Tool events are matcher-scoped; lifecycle events are not.
                    entry["matcher"] = matcher;
                }
                entry["hooks"] = new JsonArray(handler);
                entries.Add(entry);
                changed = true;
            }

            if (entries.Count == 0)
            {
                hooks.Remove(eventName);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        if (root["hooks"] is JsonObject empty && empty.Count == 0)
        {
            root.Remove("hooks");
        }

        if (uninstall && root.Count == 0 && File.Exists(marker))
        {
            File.Delete(path);
            DeleteFileIfPresent(marker);
            return;
        }

        WriteObject(path, root);
        if (!uninstall && !existed)
        {
            WriteOwnershipMarker(marker);
        }
    }

    private static void UpdateCodex(string home, string? executable, bool uninstall)
    {
        var path = SafeUserPath(home, ".codex", "hooks.json");
        var marker = path + HookFileMarkerSuffix;
        var existed = File.Exists(path);
        var root = LoadObject(path, create: !uninstall);
        if (root is null)
        {
            DeleteFileIfPresent(marker);
            return;
        }

        var changed = false;
        var hooks = GetHooksObject(root, path, create: !uninstall, ref changed);
        if (hooks is null)
        {
            return;
        }

        if (!uninstall && root["description"] is null)
        {
            root["description"] = "User lifecycle hooks.";
            changed = true;
        }

        foreach (var (eventName, timeout) in CodexEvents)
        {
            var entries = GetEventArray(hooks, eventName, create: !uninstall, ref changed);
            if (entries is null)
            {
                continue;
            }

            changed |= RemoveCodexOwnedHandlers(entries);
            if (!uninstall)
            {
                var command = BuildCommand(executable!, $"codex-hook --event {eventName}");
                var handler = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    // JSON hooks use camelCase. command_windows is only the
                    // accepted spelling in config.toml.
                    ["commandWindows"] = command,
                    ["timeout"] = timeout,
                    ["statusMessage"] = eventName == "PermissionRequest"
                        ? "Pico Vibe: waiting for touch"
                        : "Pico Vibe",
                };
                var entry = new JsonObject
                {
                    ["hooks"] = new JsonArray(handler),
                };
                // Omitting matcher applies the handler to every event reason,
                // which is exactly what these lifecycle handlers need.
                entries.Add(entry);
                changed = true;
            }

            if (entries.Count == 0)
            {
                hooks.Remove(eventName);
                changed = true;
            }
        }

        if (uninstall)
        {
            PruneInstallerOnlyDocument(root, isCursor: false, path, marker, ref changed);
        }
        else if (changed)
        {
            WriteObject(path, root);
            if (!existed)
            {
                WriteOwnershipMarker(marker);
            }
        }
    }

    private static JsonObject? LoadObject(string path, bool create)
    {
        if (!File.Exists(path))
        {
            return create ? new JsonObject() : null;
        }

        var node = JsonNode.Parse(
            File.ReadAllText(path),
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        return node as JsonObject
            ?? throw new InvalidDataException($"Expected a JSON object in {path}.");
    }

    private static void ValidateExistingHookFiles(string home)
    {
        ValidateExistingHookFile(SafeUserPath(home, ".cursor", "hooks.json"));
        ValidateExistingHookFile(SafeUserPath(home, ".codex", "hooks.json"));
        ValidateExistingHookFile(SafeUserPath(home, ".claude", "settings.json"));
    }

    private static void ValidateExistingHookFile(string path)
    {
        var root = LoadObject(path, create: false);
        if (root?["hooks"] is not null and not JsonObject)
        {
            throw new InvalidDataException($"Expected 'hooks' to be an object in {path}.");
        }
    }

    private static void ThrowIfIsolatedTestFailure(string point)
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(TestHomeEnvironmentVariable)))
        {
            return;
        }

        var requested = Environment.GetEnvironmentVariable(TestFailurePointEnvironmentVariable);
        if (string.Equals(requested, point, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Injected isolated Hook install failure at '{point}'.");
        }
    }

    private static JsonObject? GetHooksObject(
        JsonObject root,
        string path,
        bool create,
        ref bool changed)
    {
        if (root["hooks"] is JsonObject hooks)
        {
            return hooks;
        }
        if (root["hooks"] is not null)
        {
            throw new InvalidDataException($"Expected 'hooks' to be an object in {path}.");
        }
        if (!create)
        {
            return null;
        }

        hooks = new JsonObject();
        root["hooks"] = hooks;
        changed = true;
        return hooks;
    }

    private static JsonArray? GetEventArray(
        JsonObject hooks,
        string name,
        bool create,
        ref bool changed)
    {
        if (hooks[name] is JsonArray array)
        {
            return array;
        }
        if (hooks[name] is { } existing)
        {
            hooks.Remove(name);
            array = new JsonArray(existing);
            hooks[name] = array;
            changed = true;
            return array;
        }
        if (!create)
        {
            return null;
        }

        array = new JsonArray();
        hooks[name] = array;
        changed = true;
        return array;
    }

    private static bool RemoveCursorOwnedEntries(JsonArray entries)
    {
        var changed = false;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (ContainsOwnedCommand(entries[index]))
            {
                entries.RemoveAt(index);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Codex groups one or more handlers beneath a matcher. Remove only our
    /// handler so another handler sharing that group is never discarded.
    /// </summary>
    private static bool RemoveCodexOwnedHandlers(JsonArray entries)
    {
        var changed = false;
        for (var entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
        {
            if (entries[entryIndex] is not JsonObject group ||
                group["hooks"] is not JsonArray handlers)
            {
                if (ContainsOwnedCommand(entries[entryIndex]))
                {
                    entries.RemoveAt(entryIndex);
                    changed = true;
                }
                continue;
            }

            var removedHandler = false;
            for (var handlerIndex = handlers.Count - 1; handlerIndex >= 0; handlerIndex--)
            {
                if (ContainsOwnedCommand(handlers[handlerIndex]))
                {
                    handlers.RemoveAt(handlerIndex);
                    removedHandler = true;
                    changed = true;
                }
            }
            if (removedHandler && handlers.Count == 0)
            {
                entries.RemoveAt(entryIndex);
            }
        }
        return changed;
    }

    private static bool ContainsOwnedCommand(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            if (value["command"] is JsonValue commandValue &&
                commandValue.TryGetValue<string>(out var command) &&
                IsOwnedCommand(command))
            {
                return true;
            }
            return value.Any(pair => ContainsOwnedCommand(pair.Value));
        }
        return node is JsonArray array && array.Any(ContainsOwnedCommand);
    }

    private static bool IsOwnedCommand(string command) =>
        command.Contains("PicoVibeHook\"", StringComparison.OrdinalIgnoreCase) ||
        command.EndsWith("PicoVibeHook", StringComparison.OrdinalIgnoreCase) ||
        command.Contains("PicoVibeHook.exe", StringComparison.OrdinalIgnoreCase) ||
        command.Contains("PicoVibeHook.dll", StringComparison.OrdinalIgnoreCase) ||
        command.Contains("pico_ai_status.py", StringComparison.OrdinalIgnoreCase);

    private static void PruneInstallerOnlyDocument(
        JsonObject root,
        bool isCursor,
        string path,
        string marker,
        ref bool changed)
    {
        var createdByInstaller = File.Exists(marker);
        if (!changed && !createdByInstaller)
        {
            return;
        }

        if (root["hooks"] is JsonObject hooks && hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        var defaultOnly = isCursor
            ? root.Count == 1 && root["version"]?.GetValue<int>() == 1
            : root.Count == 1 &&
              string.Equals(
                  root["description"]?.GetValue<string>(),
                  "User lifecycle hooks.",
                  StringComparison.Ordinal);
        if (createdByInstaller && (root.Count == 0 || defaultOnly))
        {
            File.Delete(path);
            DeleteFileIfPresent(marker);
            return;
        }

        if (changed)
        {
            WriteObject(path, root);
        }
        DeleteFileIfPresent(marker);
    }

    private static void WriteOwnershipMarker(string path)
    {
        File.WriteAllText(
            path,
            "Created by Pico Vibe; safe to remove when its hooks are empty.\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteObject(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Hook config path has no parent.");
        Directory.CreateDirectory(directory);

        var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine;
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal))
        {
            return;
        }

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void InstallSkill(string home, string source)
    {
        var destination = SafeUserPath(home, ".codex", "skills", "pico-vibe-status");
        AssertSkillDestinationIsReplaceable(destination);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".pico-vibe-status.{Guid.NewGuid():N}.stage");
        var backup = Path.Combine(parent, $".pico-vibe-status.{Guid.NewGuid():N}.backup");
        try
        {
            CopyDirectoryWithoutLinks(source, staging);
            File.WriteAllText(
                Path.Combine(staging, SkillMarkerFile),
                "Managed by Pico Vibe.\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (Directory.Exists(destination))
            {
                MoveDirectoryWithRetry(destination, backup);
            }
            try
            {
                MoveDirectoryWithRetry(staging, destination);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(destination))
                {
                    MoveDirectoryWithRetry(backup, destination);
                }
                throw;
            }
            if (Directory.Exists(backup))
            {
                DeleteDirectoryBestEffort(backup);
            }
        }
        finally
        {
            DeleteDirectoryBestEffort(staging);
            if (Directory.Exists(backup) && Directory.Exists(destination))
            {
                DeleteDirectoryBestEffort(backup);
            }
        }
    }

    private static void CopyDirectoryWithoutLinks(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Skill source contains a directory link: {source}");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Skill source contains a file link: {file.FullName}");
            }
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: true);
        }
        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            CopyDirectoryWithoutLinks(
                directory.FullName,
                Path.Combine(destination, directory.Name));
        }
    }

    private static string? ValidateSkillSource(string home, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        source = Path.GetFullPath(source);
        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, "SKILL.md")))
        {
            throw new DirectoryNotFoundException($"Pico Vibe skill not found: {source}");
        }
        var destination = SafeUserPath(home, ".codex", "skills", "pico-vibe-status");
        if (PathsOverlap(source, destination))
        {
            throw new InvalidOperationException("Skill source and destination must not overlap.");
        }
        ValidateDirectoryWithoutLinks(source);
        AssertSkillDestinationIsReplaceable(destination);
        return source;
    }

    private static void AssertSkillDestinationIsReplaceable(string destination)
    {
        if (!Directory.Exists(destination))
        {
            return;
        }

        ValidateDirectoryWithoutLinks(destination);
        if (File.Exists(Path.Combine(destination, SkillMarkerFile)) ||
            IsRecognizedLegacySkill(destination))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The Codex Skill directory '{destination}' already exists and is not managed " +
            "by Pico Vibe. It was preserved. Move or rename that directory, then run the " +
            "installer again.");
    }

    private static void ValidateDirectoryWithoutLinks(string source)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Skill source contains a directory link: {source}");
        }
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Skill source contains a file link: {file.FullName}");
            }
        }
        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            ValidateDirectoryWithoutLinks(directory.FullName);
        }
    }

    private static void DeleteLegacyScripts(string home)
    {
        DeleteFileIfPresent(SafeUserPath(home, ".cursor", "hooks", "pico_ai_status.py"));
        DeleteFileIfPresent(SafeUserPath(home, ".codex", "hooks", "pico_ai_status.py"));
    }

    private static void DeleteFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The hook entry is already removed. A currently running legacy
            // process may keep its obsolete script locked until it exits.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve a user-protected file rather than failing an otherwise
            // valid hook migration/uninstall.
        }
    }

    private static void DeleteManagedSkill(string home)
    {
        var skill = SafeUserPath(home, ".codex", "skills", "pico-vibe-status");
        if (Directory.Exists(skill) &&
            (File.Exists(Path.Combine(skill, SkillMarkerFile)) || IsRecognizedLegacySkill(skill)))
        {
            DeleteDirectoryWithRetry(skill);
        }
    }

    private static bool IsRecognizedLegacySkill(string skill)
    {
        var definition = Path.Combine(skill, "SKILL.md");
        if (!File.Exists(definition))
        {
            return false;
        }
        try
        {
            var text = File.ReadAllText(definition);
            return text.Contains("name: pico-vibe-status", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("INSTALL_AI_VIBE.cmd", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("tools/install_ai_vibe.ps1", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("tools/pico_ai_status.py", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(40 * (attempt + 1));
            }
        }
        throw new IOException($"Could not move skill directory '{source}'.", last);
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(40 * (attempt + 1));
            }
        }
        throw new IOException($"Could not remove skill directory '{path}'.", last);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            DeleteDirectoryWithRetry(path);
        }
        catch (IOException)
        {
            // A GUID-named staging/backup directory is harmless and safer to
            // leave behind than failing after the active skill was installed.
        }
    }

    private static string ResolveExecutable(string? executableOverride)
    {
        var value = executableOverride ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine PicoVibeHook path.");
        if (value.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) >= 0)
        {
            throw new InvalidDataException("PicoVibeHook executable path contains invalid characters.");
        }

        value = Path.GetFullPath(value);
        if (!File.Exists(value))
        {
            throw new FileNotFoundException("PicoVibeHook executable was not found.", value);
        }
        return value;
    }

    internal static string BuildCommand(string executable, string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return $"\"{executable}\" {arguments}";
        }

        // Interactive Codex executes command hooks through the user's
        // PowerShell. A quoted executable followed by arguments is only a
        // string expression in PowerShell and exits with a parser error before
        // PicoVibeHook can start. Launch an explicit non-interactive Windows
        // PowerShell so the same hook string also works when the outer host is
        // cmd.exe. Single-quoted PowerShell paths support spaces; apostrophes
        // are escaped by doubling them.
        var quotedExecutable = executable.Replace("'", "''", StringComparison.Ordinal);
        return "powershell.exe -NoLogo -NoProfile -NonInteractive " +
            "-ExecutionPolicy Bypass -Command \"& '" + quotedExecutable +
            "' " + arguments + "\"";
    }

    private static string UserHome()
    {
        var overridePath = Environment.GetEnvironmentVariable(TestHomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && !Path.IsPathFullyQualified(overridePath))
        {
            throw new InvalidDataException(
                $"{TestHomeEnvironmentVariable} must be an absolute path.");
        }
        var value = string.IsNullOrWhiteSpace(overridePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : overridePath;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Cannot determine the current user's profile directory.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Pico Vibe user-home path must not be a filesystem root.");
        }
        return full;
    }

    private static string SafeUserPath(string home, params string[] components)
    {
        var candidate = Path.GetFullPath(Path.Combine(new[] { home }.Concat(components).ToArray()));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)) +
            Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A Pico Vibe install path escaped the user profile.");
        }
        return candidate;
    }

    private static bool PathsOverlap(string left, string right)
    {
        var leftPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var rightPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(leftPrefix, rightPrefix, StringComparison.OrdinalIgnoreCase) ||
            leftPrefix.StartsWith(rightPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            rightPrefix.StartsWith(leftPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IDisposable AcquireInstallLock(string home)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home)))[..16];
        string mutexName = OperatingSystem.IsWindows()
            ? $"Local\\PicoVibe.HookInstaller.{hash}"
            : $"PicoVibe.HookInstaller.{hash}";
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
            {
                throw new TimeoutException("Another Pico Vibe hook installation is still running.");
            }
            return new MutexLease(mutex);
        }
        catch
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
