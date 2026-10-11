# Pico Vibe third-party notices

Pico Vibe is distributed as a self-contained .NET application. This notice covers the
redistributable software included in the customer package.

## .NET Runtime and Windows Desktop Runtime

Copyright (c) .NET Foundation and Contributors

Licensed under the MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR
THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Source and license: https://github.com/dotnet/runtime

## Avalonia 11.3.20

The macOS user interface includes Avalonia, Avalonia.Desktop and
Avalonia.Themes.Fluent. Copyright (c) AvaloniaUI OÜ and contributors.

Avalonia is licensed under the MIT License. The permission grant and warranty
disclaimer are the same MIT terms reproduced above for the .NET Runtime.

Source and license: https://github.com/AvaloniaUI/Avalonia

## NAudio 2.2.1 family

This covers all NAudio assemblies selected by the final Windows publish, including
NAudio, NAudio.Asio, NAudio.Core, NAudio.Midi, NAudio.Wasapi, NAudio.WinForms, and
NAudio.WinMM. Copyright 2020 Mark Heath.

Licensed under the MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR
THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Source and license: https://github.com/naudio/NAudio

## MediaRemote Adapter technique

The macOS next-track helper uses the Apple-platform-host loading technique and
MediaRemote function declarations documented by MediaRemote Adapter.

Copyright (c) 2025, Jonas van den Berg and contributors

Licensed under the BSD 3-Clause License:

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software without
   specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Source and license: https://github.com/ungive/mediaremote-adapter

The release builder also copies the exact license and third-party-notice files from
the restored .NET runtime, Windows Desktop runtime, and NAudio packages into the
`licenses/` directory. Those generated files are part of the customer package and
take precedence if a bundled dependency version changes.

Pico Vibe intentionally does not bundle LibreHardwareMonitor, WinRing0, or another
kernel driver. A temperature provider that is not available on a customer's machine is
reported as unavailable.

The release builder generates `SBOM.cdx.json` from both final `.deps.json` files. Before
sale, validate and archive that exact SBOM, then Authenticode-sign `PicoVibe.exe`,
`PicoVibeHook.exe`, and the final installer/package.
