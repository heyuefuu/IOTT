using System.Reflection;
using System.Runtime.InteropServices;
using IndustrialIoT.Protocols.JingDiao;

namespace IndustrialIoT.JingDiaoShim;

internal sealed record JingDiaoSdkHealth(bool IsHealthy, string? Error)
{
    public static JingDiaoSdkHealth Probe(IJdMonApi api)
    {
        try
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
                throw new PlatformNotSupportedException("JingDiao SDK requires an x86 process.");

            var library = NativeLibrary.Load("NcMonIO.dll", typeof(NativeJdMonApi).Assembly, null);
            try
            {
                foreach (var method in typeof(NativeJdMonApi).GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var import = method.GetCustomAttribute<DllImportAttribute>();
                    if (import?.Value == "NcMonIO.dll")
                        NativeLibrary.GetExport(library, import.EntryPoint ?? method.Name);
                }

                var handle = api.Create();
                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException("CreateJDMachMon returned null.");
                api.Delete(ref handle);
            }
            finally
            {
                NativeLibrary.Free(library);
            }
            return new(true, null);
        }
        catch (Exception error)
        {
            return new(false, $"{error.GetType().Name}: {error.Message}");
        }
    }
}
