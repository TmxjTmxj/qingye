# Third-party components

| Component | Version | License and source |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT, `AvalonEdit-LICENSE.txt`, https://github.com/icsharpcode/AvalonEdit |
| Lucide SVG icons | commit 500620a2e8123f8d1db191538886dc0c223f69a9 | ISC/MIT notices in `Lucide-LICENSE.txt`, https://github.com/lucide-icons/lucide |
| Markdig | 1.4.0 | BSD-2-Clause, `Markdig-LICENSE.txt`, https://github.com/xoofx/markdig |
| Microsoft.Web.WebView2 | 1.0.4258.31 | Microsoft software license, `WebView2-LICENSE.txt`, https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| System.Memory | 4.6.3 | MIT, `DotNet-LICENSE.txt`, https://github.com/dotnet/runtime |
| System.Numerics.Vectors | 4.6.1 | MIT, `DotNet-LICENSE.txt`, https://github.com/dotnet/runtime |
| System.Buffers | 4.6.1 | MIT, `DotNet-LICENSE.txt`, https://github.com/dotnet/runtime |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT, `DotNet-LICENSE.txt`, https://github.com/dotnet/runtime |

.NET Framework and Edge WebView2 Runtime are system-shared prerequisites; the portable package does not redistribute those runtimes. Consolas and Microsoft YaHei are system fonts, not bundled assets. Original Lucide SVGs are embedded into the executable and rendered as cached frozen WPF geometries.

The embedded Agent additionally redistributes DeepSeek Harness `@deepseek-ai/dsh@0.2.1-rc.5` (MIT; `DeepSeek-Harness-LICENSE.txt`) and Node.js 24.18.0 (`Harness/Node-LICENSE.txt`, including Node's third-party notices). Harness transitive packages and their individual license files remain under `Harness/node_modules`; exact package versions and npm integrity metadata are preserved in `Harness/package-lock.json`. This is an independent Qingye integration, not an official DeepSeek product. Disabled LibreOffice conversion binaries, TypeScript declarations and source maps are excluded from the runtime.
