# GSK RM SDK Native DLLs

Place **`gskrm.dll`** (and any sibling dependencies the SDK ships with, e.g.
`gskrm_helper.dll`, resource DLLs) in this folder. The csproj copies every
`*.dll` here to the build output with `PreserveNewest`.

## Architecture matrix

| Host target  | Required DLL arch | Notes                                                        |
| ------------ | ----------------- | ------------------------------------------------------------ |
| AnyCPU / x64 | **x64**           | Current repo default. The x86 `gskrm.dll` in `src/` is a placeholder — obtain the x64 build from GSK. |
| x86          | x86               | Only if `<PlatformTarget>x86</PlatformTarget>` is forced in the host csproj. |

Loader will throw `BadImageFormatException` / `0x8007000B` on arch mismatch.

## Calling convention

Exports have no `@N` decoration, so signatures use `CallingConvention.Cdecl`.
Verify against the official GSK RM SDK header before shipping — structs and
array sizes in `NativeGskrmApi.*.cs` are **signature-best-guess** pending the
header file.
