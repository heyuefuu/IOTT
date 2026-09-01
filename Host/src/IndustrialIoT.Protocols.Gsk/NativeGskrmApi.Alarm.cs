namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;
using System.Text;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetAlarmCount", CallingConvention = Conv)]
    private static extern int GSKRM_GetAlarmCount(IntPtr handle, out uint errorCount, out uint warnCount);

    [DllImport(DllName, EntryPoint = "GSKRM_GetAlarmInfo", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_GetAlarmInfo(IntPtr handle, int index,
        out int alarmNumber, out int alarmType, StringBuilder text, uint textLength);

    public int GetAlarmCount(int handle, out int count)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { count = 0; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetAlarmCount(native, out uint errors, out uint warnings);
        count = checked((int)(errors + warnings));
        return rc;
    }

    public int GetAlarmInfo(int handle, int index, out GskrmAlarm alarm)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
        {
            alarm = new GskrmAlarm { Code = 0, Message = "", Source = "" };
            return GskrmErrorCodes.InvalidHandle;
        }
        var text = new StringBuilder(DefaultStringBuffer);
        int rc = GSKRM_GetAlarmInfo(native, index, out int number, out int type, text, (uint)text.Capacity);
        alarm = new GskrmAlarm
        {
            Code = number,
            Message = text.ToString(),
            Source = type == 0 ? "Error" : "Warning",
            Timestamp = DateTimeOffset.UtcNow
        };
        return rc;
    }
}
