# HNC NoAdapter runtime

These five files are the runtime dependencies already used by the HNC x86 shim.
They were recovered unchanged from the workspace's existing
`bin/Debug/net8.0/win-x86` output; source and destination SHA-256 hashes were verified.
The project now copies them from this directory instead of depending on an
untracked `hncsdk_V1.32.00_正式版` extraction directory.

- `HncNetDllNoAdapterCSharp.dll`: managed HNC wrapper (file version `1.0.0.0`).
- `HncNetDllNoAdapterDll.dll`: native bridge.
- `HncNetDllNoAdatper.dll`: native HNC runtime; the spelling is the vendor filename.
- `Thrift.dll`: managed dependency (file version `0.9.3.0`).
- `ftp.dll`: native file transfer library.

Keep the vendor files together in the shim output. The shim runs as `win-x86`;
the main host can remain x64. Build or load checks do not replace controller
connection and SDK compatibility testing.
