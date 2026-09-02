# PACT Contract — M1 Windows GUI Foundation

## Goal

Create a production-oriented local repository that builds a double-clickable Windows GUI foundation for RootedAndroidGameVM without opening console windows.

## Acceptance

- A .NET 10 solution contains Core, WPF Launcher, WPF Setup and automated test projects.
- Core can run a child process with redirected output and `CreateNoWindow=true`.
- Core validates that filesystem targets remain inside an allowed root.
- Launcher starts as a Windows GUI and exposes the approved top-level actions without invoking a terminal.
- Setup starts as a Windows GUI and presents installation progress states.
- `dotnet test` passes with tests that were observed failing before implementation.
- `dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true` produces GUI executables.
- A local release audit rejects forbidden extensions and secrets.
- The repository is local only; no GitHub repository or public Release is created.

## Non-goals for M1

- Downloading the full Android SDK or system image.
- Patching an AVD ramdisk.
- Claiming compatibility with every APK.
- Publishing third-party binaries, commercial APKs or user data.
- Creating a public GitHub repository.

## Constraints

- Windows 11 x64.
- .NET 10 WPF, self-contained single-file publish.
- Inno Setup 6.7.3 for the installer stage.
- All external tools must be launched without a visible console window.
- No credentials, account data, APKs, AVD images or exported game data may enter the repository.
- Changes follow red-green-refactor and remain reviewable.
