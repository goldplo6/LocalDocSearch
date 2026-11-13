# Third-party notices

LocalDocSearch's own code is licensed under the [MIT License](LICENSE). Third-party components retain their original copyrights and licenses; the project's MIT license does not replace them.

The machine-readable [dependency inventory](docs/dependency-licenses/inventory.json) records exact versions, nuspec license metadata, repository commits, NuGet canonical content hashes, original nupkg SHA256 values, and the paths/hashes of preserved license and NOTICE texts. The [readable inventory](docs/dependency-licenses/inventory.md) covers the complete resolved Desktop graph, including build-only and non-Windows dependencies conservatively. Presence in that graph does not assert that every platform binary is distributed in win-x64.

| NuGet package | Version | Declared nuspec license |
|---|---|---|
| Avalonia | 12.1.5 | MIT |
| Avalonia.Desktop | 12.1.5 | MIT |
| Avalonia.Themes.Fluent | 12.1.5 | MIT |
| Avalonia.FreeDesktop | 12.1.5 | MIT |
| Avalonia.FreeDesktop.AtSpi | 12.1.5 | MIT |
| Avalonia.HarfBuzz | 12.1.5 | MIT |
| Avalonia.Native | 12.1.5 | MIT |
| Avalonia.Remote.Protocol | 12.1.5 | MIT |
| Avalonia.Skia | 12.1.5 | MIT |
| Avalonia.Win32 | 12.1.5 | MIT |
| Avalonia.X11 | 12.1.5 | MIT |
| Avalonia.BuildServices | 11.3.2 | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | File: LICENSE (preserved ANGLE BSD text) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| DocumentFormat.OpenXml | 3.5.1 | MIT |
| DocumentFormat.OpenXml.Framework | 3.5.1 | MIT |
| HarfBuzzSharp | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | MIT |
| MicroCom.Runtime | 0.11.6 | MIT |
| PdfPig | 0.1.16 | Apache-2.0 |
| SkiaSharp | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.WebAssembly | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT |
| System.IO.Packaging | 10.0.2 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |

## Preserved original notices

The inventory's `evidence` paths are relative to `docs/dependency-licenses/`. They contain unmodified installed package license/NOTICE material and nuspec files. Packages that omit full license text are supplemented from the exact repository commit declared by their nuspec; [source provenance and SHA256 values](docs/dependency-licenses.sources.json) record these fetches. The upstream originals include Avalonia's NOTICE, Open XML SDK's NOTICE, PdfPig's complete Apache/BSD/Adobe material, and all packaged SkiaSharp/HarfBuzzSharp native third-party notices. These texts are preserved in their original language.

Self-contained candidates add a `bundledRuntime` record and preserve the actual `Microsoft.NETCore.App.Runtime.win-x64` pack's LICENSE and third-party notices under `docs/dependency-licenses/runtime/`. The runtime version and nupkg hash are recorded by the candidate build; the checked-in framework-dependent inventory has no bundled runtime. Release generation fails when required license texts, accepted metadata, package hashes, or runtime notices are unresolved. Test-only NuGet packages are not included in the application ZIP and are outside this Desktop runtime inventory.

This is a reproducible collection of dependency metadata and notice evidence. It is not a claim of a full native-source provenance audit, public-release approval, or independent legal review. Consult the actual preserved texts for component-specific conditions.
