# Third-party notices

Quick Sweep itself is released under the [MIT license](LICENSE). The release zip bundles the libraries below, unmodified, next to `QuickSweep.exe`. Each keeps its own license.

| Library | Version | License | Source |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | https://github.com/Blacktempel/BlackSharp |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp | 2.6.4 | Apache-2.0 | https://software.seekye.com/hidsharp |
| Mono.Posix.NETStandard (with its native `MonoPosixHelper`) | 1.0.0 | MIT | https://github.com/mono/mono.posix |
| System.IO.Ports | 10.0.3 | MIT | https://github.com/dotnet/dotnet |
| System.Management | 10.0.2 | MIT | https://github.com/dotnet/dotnet |

Copies of the license texts are in the [`licenses`](licenses) folder (and in the release zip). The four MPL-2.0 libraries are used as unmodified binaries from NuGet. The Mozilla Public License 2.0 applies to those files only; it does not apply to the rest of Quick Sweep. The complete source of each of them is available at the addresses above, and the license text is at https://www.mozilla.org/MPL/2.0/. The Apache License 2.0 text for HidSharp is at https://www.apache.org/licenses/LICENSE-2.0 and ships inside the NuGet package (`LICENSE.txt`). The MIT licenses of the Microsoft and Mono packages are at https://licenses.nuget.org/MIT.

## Not bundled

- **.NET 10 Desktop Runtime** (MIT, Microsoft): installed on request by `install.ps1` through `winget`; not part of the zip.
- **PawnIO** (https://pawnio.eu, by namazso): the signed kernel driver that LibreHardwareMonitor uses to read the CPU temperature. It is installed on request by `install.ps1` through `winget` (`namazso.PawnIO`) under its own license, which you accept with that package. It is not in this repository or in the zip.
- **NVIDIA Management Library (NVML)**: `nvml.dll` ships with the NVIDIA display driver; Quick Sweep only calls it, it is not redistributed.
