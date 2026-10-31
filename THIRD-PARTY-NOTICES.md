# ETS2 Werkstatt: third-party notices

ETS2 Werkstatt 1.10 ships as one self-contained file, `ETS2-Werkstatt.exe` (Windows x64). This file lists every
third-party component bundled in that exe, and the format references named in the source code.

NuGet packages: none. `dotnet list package --include-transitive` shows only `Microsoft.NET.ILLink.Tasks`, which the
.NET SDK adds automatically. It runs at build time only and is not part of the exe.

Microsoft distributes the .NET runtime binaries bundled in the exe under the Microsoft .NET Library license terms (`Lizenzen/dotnet-LICENSE.txt` in the release archive), which allow redistributing them inside an application; their source is MIT-licensed as listed above. The runtime's third-party notices are in `Lizenzen/dotnet-ThirdPartyNotices.txt`.

## 1. Components bundled in the exe

| Component | Version | License | Copyright | Source, license and notices |
|---|---|---|---|---|
| .NET Runtime (`Microsoft.NETCore.App`: CoreCLR, base class libraries, host, and the zlib-ng based `System.IO.Compression.Native`) | 10.0.13 | MIT | © .NET Foundation and Contributors | <https://github.com/dotnet/runtime> · license <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> · third-party notices <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT> |
| .NET Windows Desktop Runtime, WPF (`Microsoft.WindowsDesktop.App`: PresentationCore, PresentationFramework, WindowsBase, `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `PenImc_cor3.dll`, and the Microsoft redistributables `D3DCompiler_47_cor3.dll` and `vcruntime140_cor3.dll` that come with it) | 10.0.13 | MIT | © .NET Foundation and Contributors | <https://github.com/dotnet/wpf> · license <https://github.com/dotnet/wpf/blob/main/LICENSE.TXT> · third-party notices <https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT> |
| MsQuic (`msquic.dll`, part of the .NET Runtime above) | as shipped with .NET 10.0.13 | MIT | © Microsoft Corporation | <https://github.com/microsoft/msquic> · <https://github.com/microsoft/msquic/blob/main/LICENSE> |
| Google CityHash v1.0.3, `CityHash64` only, ported to C# in `HashFs.cs` | 1.0.3 (commit `00b9287e8c1255b5922ef90e304d5287361b2c2a`) | MIT | © 2011 Google, Inc. | <https://github.com/google/cityhash> · license text below |

The .NET third-party notice files list the open-source code the runtime itself contains (for example zlib-ng under
the zlib license). Ship them next to the exe, or keep these links in the release notes.

### MIT License text (CityHash)

```
Copyright (c) 2011 Google, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

The MIT texts of the .NET Runtime, WPF and MsQuic are at the license links in the table (same wording, with their
own copyright lines).

## 2. Format references (no third-party code included)

These projects were used to understand file formats. No code from them is in the exe; they are credited because the
source code names them.

| Reference | License | Used for | Named in |
|---|---|---|---|
| ts-map by Dario Wouters, <https://github.com/dariowouters/ts-map> | MIT, © 2017 Dario Wouters | Cross-check of the HashFS v1/v2 layout (part kinds, compression methods, 28-bit sizes), as recorded in `HASHFS-SPEC.md` | `HashFs.cs` header |
| scs_tools (local copy `work\scs-tools`) | MIT, © 2026 scs_tools contributors | HashFS cross-check (with the 24-bit size error corrected) and the binary SII (BSII) format | `HashFs.cs` header, `BinarySii.cs` |
| TheLazyTomcat's binary SII format notes (SII_Decrypt), <https://github.com/TheLazyTomcat/SII_Decrypt> | n/a (format description only) | Binary SII structure | `BinarySii.cs` |

## 3. Removed in 1.10

Earlier versions bundled TruckLib, TruckLib.HashFs, TruckLib.Models, TruckLib.Sii and TruckLib.Core by sk-zk
(GPL-2.0) for reading game archives and the road map. 1.10 removes all of them, and the road map with them. City names
and the XP curve now come from `HashFs.cs`, which was written from `HASHFS-SPEC.md` without looking at TruckLib,
sk-zk/Extractor, truckermudgeon/maps or ConverterPIX.
