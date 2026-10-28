# Third-party notices

## Microsoft WebView2

TaskHarbor builds against **Microsoft.Web.WebView2 1.0.4191.47** and embeds its managed SDK assemblies and x64 native loader in the release executable. These components are provided by Microsoft and are **not relicensed under MIT**.

- [Official NuGet package and license information](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47)
- [Microsoft WebView2 documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/)

The restore script extracts the license distributed with that package to `build/refs/WEBVIEW2-LICENSE.txt`. Packaging includes that license alongside the executable. The independently installed WebView2 Runtime is not included in the release archive and remains subject to Microsoft's terms.

TaskHarbor's own source and first-party icon assets are covered by the repository MIT license. No external font or icon library is downloaded at runtime; the interface uses installed Windows fonts.
