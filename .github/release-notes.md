**Download `PortaStart.exe` below and run it** (double-click, or run `PortaStart` in a terminal). Or install it with winget once the package is approved: `winget install portastart`.

It makes Start menu shortcuts for the GUI apps you installed as winget portable packages, then keeps watching in the background (no window, starts at sign-in) so new installs get a shortcut and uninstalls remove it. `PortaStart remove-all` undoes everything.

No installer and no runtime to install: it uses .NET Framework 4.8, which is already part of Windows 10 and 11. This build was compiled by GitHub Actions from the tagged source; the `.sha256` file lets you verify it.

Windows SmartScreen may say "Windows protected your PC" because the exe isn't code-signed yet. Click **More info → Run anyway**, or build it yourself with `dotnet build src/PortaStart -c Release`.
