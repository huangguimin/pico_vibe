using System.Text.Json.Nodes;
using System.Diagnostics;
using PicoVibe.Hook;

namespace PicoVibe.Hook.Tests;

public sealed class HookInstallerIntegrationTests
{
    [Fact]
    public void WindowsHookCommandRunsFromPowerShellWithSpacesAndApostropheInPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tree = new TempTree();
        var executable = Path.Combine(
            tree.Directory("customer's Program Files", "Pico Vibe"),
            "PicoVibeHook.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var command = HookInstaller.BuildCommand(executable, "/d /c exit 0");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo);

        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void FreshInstallAndUninstallAreClean()
    {
        using var tree = new TempTree();
        var home = tree.Directory("new home");
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        Assert.Equal(0, HookInstaller.Install(null, executable));
        var cursorPath = Path.Combine(home, ".cursor", "hooks.json");
        var codexPath = Path.Combine(home, ".codex", "hooks.json");
        var claudePath = Path.Combine(home, ".claude", "settings.json");
        Assert.True(File.Exists(cursorPath));
        Assert.True(File.Exists(codexPath));
        Assert.True(File.Exists(claudePath));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(cursorPath))!["hooks"]!["postToolUse"]);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(claudePath))!["hooks"]!["PostToolUseFailure"]);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(claudePath))!["hooks"]!["SubagentStop"]);

        Assert.Equal(0, HookInstaller.Uninstall());
        Assert.False(File.Exists(cursorPath));
        Assert.False(File.Exists(codexPath));
        Assert.False(File.Exists(claudePath));
        Assert.False(File.Exists(cursorPath + ".pico-vibe-created"));
        Assert.False(File.Exists(codexPath + ".pico-vibe-created"));
        Assert.False(File.Exists(claudePath + ".pico-vibe-created"));
    }

    [Fact]
    public void PreexistingMinimalHookFilesAreNotDeletedOnUninstall()
    {
        using var tree = new TempTree();
        var home = tree.Directory("existing home");
        var cursorPath = Path.Combine(home, ".cursor", "hooks.json");
        var codexPath = Path.Combine(home, ".codex", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
        File.WriteAllText(cursorPath, "{\"version\":1,\"hooks\":{}}");
        File.WriteAllText(codexPath, "{\"description\":\"User lifecycle hooks.\",\"hooks\":{}}");
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        HookInstaller.Install(null, executable);
        HookInstaller.Uninstall();

        Assert.True(File.Exists(cursorPath));
        Assert.True(File.Exists(codexPath));
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(cursorPath))!["version"]!.GetValue<int>());
        Assert.Equal(
            "User lifecycle hooks.",
            JsonNode.Parse(File.ReadAllText(codexPath))!["description"]!.GetValue<string>());
    }

    [Fact]
    public void UninstallPreservesAnUnmarkedForeignSkillDirectory()
    {
        using var tree = new TempTree();
        var home = tree.Directory("home");
        var skill = tree.Directory("home", ".codex", "skills", "pico-vibe-status");
        var definition = Path.Combine(skill, "SKILL.md");
        File.WriteAllText(definition, "---\nname: another-skill\n---\nDo not remove.\n");
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        HookInstaller.Uninstall();

        Assert.True(File.Exists(definition));
    }

    [Fact]
    public void InstallRejectsAnUnmanagedSameNameSkillWithoutChangingHooksOrSkill()
    {
        using var tree = new TempTree();
        var home = tree.Directory("home");
        var skillSource = CreateSkillSource(tree);
        var installedSkill = tree.Directory("home", ".codex", "skills", "pico-vibe-status");
        var definition = Path.Combine(installedSkill, "SKILL.md");
        const string customDefinition =
            "---\nname: pico-vibe-status\n---\n# My custom Pico Vibe helper\nDo not replace.\n";
        File.WriteAllText(definition, customDefinition);
        File.WriteAllText(Path.Combine(installedSkill, "keep.txt"), "customer-owned");
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            HookInstaller.Install(skillSource, executable));

        Assert.Contains("is not managed by Pico Vibe", exception.Message, StringComparison.Ordinal);
        Assert.Equal(customDefinition, File.ReadAllText(definition));
        Assert.Equal("customer-owned", File.ReadAllText(Path.Combine(installedSkill, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(home, ".cursor", "hooks.json")));
        Assert.False(File.Exists(Path.Combine(home, ".codex", "hooks.json")));
    }

    [Fact]
    public void InstallUpgradesAManagedSkill()
    {
        using var tree = new TempTree();
        var home = tree.Directory("home");
        var skillSource = CreateSkillSource(tree);
        var installedSkill = tree.Directory("home", ".codex", "skills", "pico-vibe-status");
        File.WriteAllText(Path.Combine(installedSkill, "SKILL.md"), "old managed skill");
        File.WriteAllText(Path.Combine(installedSkill, ".pico-vibe-managed"), "Managed by Pico Vibe.\n");
        File.WriteAllText(Path.Combine(installedSkill, "obsolete.txt"), "remove me");
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        Assert.Equal(0, HookInstaller.Install(skillSource, executable));

        Assert.Contains("# Test skill", File.ReadAllText(Path.Combine(installedSkill, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(installedSkill, ".pico-vibe-managed")));
        Assert.False(File.Exists(Path.Combine(installedSkill, "obsolete.txt")));
    }

    [Fact]
    public void IsolatedFailureInjectionCanLeaveAPartialInstallForTransactionTesting()
    {
        using var tree = new TempTree();
        var home = tree.Directory("home");
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");
        using var homeEnvironment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);
        using var failureEnvironment = new EnvironmentVariableScope(
            HookInstaller.TestFailurePointEnvironmentVariable,
            HookInstaller.TestFailureAfterCursorUpdate);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            HookInstaller.Install(null, executable));

        Assert.Contains("Injected isolated Hook install failure", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "PicoVibeHook",
            File.ReadAllText(Path.Combine(home, ".cursor", "hooks.json")),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(home, ".codex", "hooks.json")));
        Assert.Equal(0, HookInstaller.Uninstall());
        Assert.False(File.Exists(Path.Combine(home, ".cursor", "hooks.json")));
    }

    [Fact]
    public void InstallReinstallAndUninstallPreserveUnrelatedConfiguration()
    {
        using var tree = new TempTree();
        var home = tree.Directory("fake home");
        var skillSource = CreateSkillSource(tree);

        var executable = Path.Combine(tree.Directory("Program Files", "Pico Vibe"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "test executable placeholder");

        var cursorPath = Path.Combine(home, ".cursor", "hooks.json");
        var codexPath = Path.Combine(home, ".codex", "hooks.json");
        var claudePath = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(claudePath)!);
        File.WriteAllText(cursorPath, CursorSeed);
        File.WriteAllText(codexPath, CodexSeed);
        File.WriteAllText(claudePath, ClaudeSeed);

        var legacyCursor = Path.Combine(home, ".cursor", "hooks", "pico_ai_status.py");
        var legacyCodex = Path.Combine(home, ".codex", "hooks", "pico_ai_status.py");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyCursor)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyCodex)!);
        File.WriteAllText(legacyCursor, "legacy");
        File.WriteAllText(legacyCodex, "legacy");
        var installedSkill = Path.Combine(home, ".codex", "skills", "pico-vibe-status");
        Directory.CreateDirectory(installedSkill);
        File.WriteAllText(
            Path.Combine(installedSkill, "SKILL.md"),
            "---\nname: pico-vibe-status\n---\n" +
            "Run INSTALL_AI_VIBE.cmd or tools/install_ai_vibe.ps1.\n" +
            "Diagnose with tools/pico_ai_status.py.\n");

        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        Assert.Equal(0, HookInstaller.Install(skillSource, executable));
        Assert.False(File.Exists(legacyCursor));
        Assert.False(File.Exists(legacyCodex));
        AssertInstalledConfiguration(cursorPath, codexPath, claudePath, executable);

        Assert.True(File.Exists(Path.Combine(installedSkill, ".pico-vibe-managed")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(installedSkill, "references", "keep.txt")));

        var cursorAfterFirstInstall = File.ReadAllBytes(cursorPath);
        var codexAfterFirstInstall = File.ReadAllBytes(codexPath);
        var claudeAfterFirstInstall = File.ReadAllBytes(claudePath);
        Assert.Equal(0, HookInstaller.Install(skillSource, executable));
        Assert.Equal(cursorAfterFirstInstall, File.ReadAllBytes(cursorPath));
        Assert.Equal(codexAfterFirstInstall, File.ReadAllBytes(codexPath));
        Assert.Equal(claudeAfterFirstInstall, File.ReadAllBytes(claudePath));
        AssertInstalledConfiguration(cursorPath, codexPath, claudePath, executable);

        Assert.Equal(0, HookInstaller.Uninstall());
        Assert.False(Directory.Exists(installedSkill));
        AssertUninstalledConfiguration(cursorPath, codexPath, claudePath);

        var cursorAfterUninstall = File.ReadAllBytes(cursorPath);
        var codexAfterUninstall = File.ReadAllBytes(codexPath);
        var claudeAfterUninstall = File.ReadAllBytes(claudePath);
        Assert.Equal(0, HookInstaller.Uninstall());
        Assert.Equal(cursorAfterUninstall, File.ReadAllBytes(cursorPath));
        Assert.Equal(codexAfterUninstall, File.ReadAllBytes(codexPath));
        Assert.Equal(claudeAfterUninstall, File.ReadAllBytes(claudePath));
    }

    [Fact]
    public void InvalidHooksShapeIsRejectedWithoutOverwritingTheFile()
    {
        using var tree = new TempTree();
        var home = tree.Directory("home");
        var cursorPath = Path.Combine(home, ".cursor", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        const string invalid = "{\"version\":1,\"hooks\":[],\"keep\":42}";
        File.WriteAllText(cursorPath, invalid);
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");

        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);
        Assert.Throws<InvalidDataException>(() => HookInstaller.Install(null, executable));
        Assert.Equal(invalid, File.ReadAllText(cursorPath));
    }

    [Fact]
    public void InvalidCodexFileDoesNotPartiallyModifyCursor()
    {
        using var tree = new TempTree();
        var home = tree.Directory("home");
        var cursorPath = Path.Combine(home, ".cursor", "hooks.json");
        var codexPath = Path.Combine(home, ".codex", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
        const string cursor = "{\"version\":1,\"hooks\":{\"custom\":[{\"command\":\"keep.exe\"}]}}";
        const string codex = "{\"hooks\":[]}";
        File.WriteAllText(cursorPath, cursor);
        File.WriteAllText(codexPath, codex);
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        Assert.Throws<InvalidDataException>(() => HookInstaller.Install(null, executable));
        Assert.Equal(cursor, File.ReadAllText(cursorPath));
        Assert.Equal(codex, File.ReadAllText(codexPath));
    }

    [Fact]
    public void MissingSkillSourceDoesNotPartiallyInstallHooks()
    {
        using var tree = new TempTree();
        var home = tree.Directory("home");
        var executable = Path.Combine(tree.Directory("bin"), "PicoVibeHook.exe");
        File.WriteAllText(executable, "placeholder");
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            home);

        Assert.Throws<DirectoryNotFoundException>(() =>
            HookInstaller.Install(Path.Combine(tree.Root, "missing-skill"), executable));
        Assert.False(File.Exists(Path.Combine(home, ".cursor", "hooks.json")));
        Assert.False(File.Exists(Path.Combine(home, ".codex", "hooks.json")));
    }

    [Fact]
    public void TestHomeCannotBeAFileSystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(root));
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            root);
        Assert.Throws<InvalidDataException>(() => HookInstaller.Uninstall());
    }

    [Fact]
    public void TestHomeMustBeAbsolute()
    {
        using var environment = new EnvironmentVariableScope(
            HookInstaller.TestHomeEnvironmentVariable,
            "relative-test-home");
        Assert.Throws<InvalidDataException>(() => HookInstaller.Uninstall());
    }

    private static void AssertInstalledConfiguration(
        string cursorPath,
        string codexPath,
        string claudePath,
        string executable)
    {
        var cursor = JsonNode.Parse(File.ReadAllText(cursorPath))!.AsObject();
        Assert.Equal(7, cursor["version"]!.GetValue<int>());
        Assert.Equal("preserve", cursor["custom"]!["name"]!.GetValue<string>());
        Assert.Equal(
            "unrelated-event.exe",
            cursor["hooks"]!["customEvent"]!["command"]!.GetValue<string>());
        var cursorShell = cursor["hooks"]!["beforeShellExecution"]!.AsArray();
        Assert.Contains(cursorShell, item =>
            item!["command"]!.GetValue<string>() == "keep-cursor.exe --audit");
        Assert.Single(cursorShell, item => IsInstalled(item, executable));
        Assert.DoesNotContain("pico_ai_status.py", cursor.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var codex = JsonNode.Parse(File.ReadAllText(codexPath))!.AsObject();
        Assert.Equal("Preserve this description", codex["description"]!.GetValue<string>());
        Assert.True(codex["custom"]!["enabled"]!.GetValue<bool>());
        var permissionGroups = codex["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Contains(permissionGroups, group =>
            group!["hooks"]!.AsArray().Any(handler =>
                handler!["command"]!.GetValue<string>() == "keep-codex.exe --audit"));
        var installedHandlers = permissionGroups
            .SelectMany(group => group!["hooks"]!.AsArray())
            .Where(handler => IsInstalled(handler, executable))
            .ToArray();
        Assert.Single(installedHandlers);
        Assert.NotNull(installedHandlers[0]!["commandWindows"]);
        Assert.Null(installedHandlers[0]!["command_windows"]);
        var installedGroup = permissionGroups.Single(group =>
            group!["hooks"]!.AsArray().Any(handler => IsInstalled(handler, executable)));
        Assert.Null(installedGroup!["matcher"]);
        Assert.DoesNotContain("pico_ai_status.py", codex.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"matcher\": \"*\"", codex.ToJsonString(), StringComparison.Ordinal);

        var sessionEndHandler = codex["hooks"]!["SessionEnd"]!.AsArray()
            .SelectMany(group => group!["hooks"]!.AsArray())
            .Single(handler => IsInstalled(handler, executable));
        Assert.Equal(3, sessionEndHandler!["timeout"]!.GetValue<int>());

        var claude = JsonNode.Parse(File.ReadAllText(claudePath))!.AsObject();
        Assert.Equal("opus", claude["model"]!.GetValue<string>());
        Assert.Equal(
            "Bash(git status:*)",
            claude["permissions"]!["allow"]![0]!.GetValue<string>());
        var claudePermissionGroups = claude["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Contains(claudePermissionGroups, group =>
            group!["hooks"]!.AsArray().Any(handler =>
                handler!["command"]!.GetValue<string>() == "keep-claude.exe --audit"));
        var claudeInstalledHandlers = claudePermissionGroups
            .SelectMany(group => group!["hooks"]!.AsArray())
            .Where(handler => IsInstalled(handler, executable))
            .ToArray();
        Assert.Single(claudeInstalledHandlers);
        Assert.Equal(605, claudeInstalledHandlers[0]!["timeout"]!.GetValue<int>());
        Assert.Contains(
            "claude-hook --event PermissionRequest",
            claudeInstalledHandlers[0]!["command"]!.GetValue<string>(),
            StringComparison.Ordinal);
        var claudeInstalledGroup = claudePermissionGroups.Single(group =>
            group!["hooks"]!.AsArray().Any(handler => IsInstalled(handler, executable)));
        Assert.Equal("*", claudeInstalledGroup!["matcher"]!.GetValue<string>());
        var notificationHandlers = claude["hooks"]!["Notification"]!.AsArray()
            .SelectMany(group => group!["hooks"]!.AsArray())
            .ToArray();
        Assert.Single(notificationHandlers);
        Assert.Equal(
            "keep-claude-notification.exe --audit",
            notificationHandlers[0]!["command"]!.GetValue<string>());
        Assert.DoesNotContain("pico_ai_status.py", claude.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertUninstalledConfiguration(
        string cursorPath,
        string codexPath,
        string claudePath)
    {
        var cursor = JsonNode.Parse(File.ReadAllText(cursorPath))!.AsObject();
        Assert.Equal(7, cursor["version"]!.GetValue<int>());
        Assert.Equal("preserve", cursor["custom"]!["name"]!.GetValue<string>());
        Assert.Equal(
            "keep-cursor.exe --audit",
            cursor["hooks"]!["beforeShellExecution"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(
            "unrelated-event.exe",
            cursor["hooks"]!["customEvent"]!["command"]!.GetValue<string>());
        Assert.DoesNotContain("PicoVibeHook", cursor.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var codex = JsonNode.Parse(File.ReadAllText(codexPath))!.AsObject();
        Assert.Equal("Preserve this description", codex["description"]!.GetValue<string>());
        Assert.True(codex["custom"]!["enabled"]!.GetValue<bool>());
        var handlers = codex["hooks"]!["PermissionRequest"]!.AsArray()
            .SelectMany(group => group!["hooks"]!.AsArray())
            .ToArray();
        Assert.Single(handlers);
        Assert.Equal("keep-codex.exe --audit", handlers[0]!["command"]!.GetValue<string>());
        Assert.Equal(
            "unrelated-stop.exe",
            codex["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.DoesNotContain("PicoVibeHook", codex.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var claude = JsonNode.Parse(File.ReadAllText(claudePath))!.AsObject();
        Assert.Equal("opus", claude["model"]!.GetValue<string>());
        Assert.Equal(
            "Bash(git status:*)",
            claude["permissions"]!["allow"]![0]!.GetValue<string>());
        var claudePermissionHandlers = claude["hooks"]!["PermissionRequest"]!.AsArray()
            .SelectMany(group => group!["hooks"]!.AsArray())
            .ToArray();
        Assert.Single(claudePermissionHandlers);
        Assert.Equal(
            "keep-claude.exe --audit",
            claudePermissionHandlers[0]!["command"]!.GetValue<string>());
        Assert.Equal(
            "keep-claude-notification.exe --audit",
            claude["hooks"]!["Notification"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.DoesNotContain("PicoVibeHook", claude.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pico_ai_status.py", claude.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstalled(JsonNode? node, string executable) =>
        node?["command"]?.GetValue<string>().Contains(executable, StringComparison.OrdinalIgnoreCase) == true;

    private static string CreateSkillSource(TempTree tree)
    {
        var skillSource = tree.Directory("skill source");
        Directory.CreateDirectory(Path.Combine(skillSource, "references"));
        File.WriteAllText(
            Path.Combine(skillSource, "SKILL.md"),
            "---\nname: pico-vibe-status\n---\n# Test skill\n");
        File.WriteAllText(Path.Combine(skillSource, "references", "keep.txt"), "nested");
        return skillSource;
    }

    private const string CursorSeed = """
        {
          "version": 7,
          "custom": { "name": "preserve" },
          "hooks": {
            "beforeShellExecution": [
              { "command": "keep-cursor.exe --audit", "timeout": 11 },
              { "command": "python hooks/pico_ai_status.py hook ask --event beforeShellExecution" }
            ],
            "customEvent": { "command": "unrelated-event.exe" }
          }
        }
        """;

    private const string CodexSeed = """
        {
          "description": "Preserve this description",
          "custom": { "enabled": true },
          "hooks": {
            "PermissionRequest": [
              {
                "matcher": "Bash",
                "hooks": [
                  { "type": "command", "command": "keep-codex.exe --audit" },
                  { "type": "command", "command": "python .codex/hooks/pico_ai_status.py codex-hook --event PermissionRequest" }
                ]
              }
            ],
            "Stop": [
              { "hooks": [ { "type": "command", "command": "unrelated-stop.exe" } ] }
            ]
          }
        }
        """;

    private const string ClaudeSeed = """
        {
          "model": "opus",
          "permissions": { "allow": [ "Bash(git status:*)" ] },
          "hooks": {
            "PermissionRequest": [
              {
                "matcher": "Bash",
                "hooks": [
                  { "type": "command", "command": "keep-claude.exe --audit" },
                  { "type": "command", "command": "python .claude/hooks/pico_ai_status.py claude-hook --event PermissionRequest" }
                ]
              }
            ],
            "Notification": [
              {
                "matcher": "idle_prompt",
                "hooks": [
                  { "type": "command", "command": "keep-claude-notification.exe --audit" },
                  { "type": "command", "command": "python .claude/hooks/pico_ai_status.py claude-hook --event Notification" }
                ]
              }
            ],
            "Stop": [
              { "hooks": [ { "type": "command", "command": "keep-claude-stop.exe" } ] }
            ]
          }
        }
        """;

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            this.name = name;
            previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, previous);
    }

    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "PicoVibeHookTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] components)
        {
            var path = Path.Combine(new[] { Root }.Concat(components).ToArray());
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
