# Third-party components

The source code uses only the Windows/.NET platform libraries; it does not require third-party NuGet application libraries.

Self-contained distributions include Microsoft .NET and Windows Desktop Runtime 8.0.33. Their licenses and third-party notices are copied to `licenses/` by the publish target. Retain these files when distributing the executable.

- [.NET runtime licensing](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- [.NET runtime third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)
- [Windows Desktop runtime license](https://github.com/dotnet/windowsdesktop/blob/main/LICENSE)
- [WPF license](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)

WinGet is an external dependency supplied by Microsoft App Installer and is not bundled with this application. Program installers obtained through WinGet or custom sources retain their own licenses.
