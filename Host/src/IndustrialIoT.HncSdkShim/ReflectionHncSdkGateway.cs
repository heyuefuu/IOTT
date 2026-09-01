namespace IndustrialIoT.HncSdkShim;

using System.Collections.Concurrent;
using System.Reflection;
using IndustrialIoT.Protocols.HncSdk;

public sealed class ReflectionHncSdkGateway : IHncSdkGateway
{
    private const int Ok = 0;
    private const int Error = -1;
    private readonly ConcurrentDictionary<string, HncSdkSession> sessions = new();
    private readonly Type? hncApiType;
    private readonly object netInitLock = new();
    private volatile bool netInitDone;

    public ReflectionHncSdkGateway()
    {
        // NoAdapter 版 dll；若日后切回有适配器版只需替换 csproj 引用并改回这里的文件名。
        var sdkPath = Path.Combine(AppContext.BaseDirectory, "HncNetDllNoAdapterCSharp.dll");
        if (File.Exists(sdkPath))
        {
            var sdkAssembly = Assembly.LoadFrom(sdkPath);
            hncApiType = sdkAssembly.GetType("HncNetDllNoAdapterCSharp.HncApi")
                      ?? sdkAssembly.GetType("HNCAPI_INTERFACE.HncApi");
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryStaticCall("HNC_NetExit");
    }

    public Task<HncSdkConnectResult> ConnectAsync(HncSdkConnectRequest request, CancellationToken ct)
    {
        if (hncApiType is null)
            return Task.FromResult(new HncSdkConnectResult
            {
                ReturnCode = Error,
                ErrorMessage = "HNC SDK runtime unavailable. Copy HncNetDllNoAdapterCSharp.dll (and native deps) to shim output.",
            });

        try
        {
            var initRc = EnsureNetInit(request);
            if (initRc != Ok)
                return Task.FromResult(FailConnect(initRc, "HNC_NetInit failed"));

            var api = Activator.CreateInstance(hncApiType)!;
            var connect = CallByArity(api, "HNC_NetConnect", request.Host, (ushort)request.Port);
            var machineNo = Convert.ToInt32(connect);
            if (machineNo < 0 || machineNo >= 255) return Task.FromResult(FailConnect(machineNo, "HNC_NetConnect failed"));

            TryCall(api, "HNC_AlarmSubscribe", false);
            TryCall(api, "HNC_EventSubscribe", false);
            // NoAdapter 版 demo 不再调用 HNC_ClientRequestWriteToken；保留默认即可。

            var sessionId = Guid.NewGuid().ToString("N");
            sessions[sessionId] = new HncSdkSession(api, request.Host);
            return Task.FromResult(new HncSdkConnectResult { ReturnCode = Ok, SessionId = sessionId });
        }
        catch (Exception ex)
        {
            return Task.FromResult(FailConnect(Error, ex.Message));
        }
    }

    private int EnsureNetInit(HncSdkConnectRequest request)
    {
        if (netInitDone) return Ok;
        lock (netInitLock)
        {
            if (netInitDone) return Ok;
            var method = hncApiType!.GetMethod("HNC_NetInit", BindingFlags.Public | BindingFlags.Static);
            if (method is null) return Error;
            var parameters = method.GetParameters();
            object? rc = parameters.Length switch
            {
                2 => method.Invoke(null, [request.LocalIp, (ushort)request.LocalPort]),
                3 => method.Invoke(null, [request.LocalIp, (ushort)request.LocalPort, request.ClientName]),
                _ => Error,
            };
            var converted = Convert.ToInt32(rc);
            if (converted == Ok) netInitDone = true;
            return converted;
        }
    }

    public Task<HncSdkIpcResult> DisconnectAsync(string sessionId, CancellationToken ct)
    {
        // HNC_NetExit 是进程级，多会话场景下只能进程退出时统一断；这里仅释放会话槽位。
        sessions.TryRemove(sessionId, out _);
        return Task.FromResult(new HncSdkIpcResult { ReturnCode = Ok });
    }

    public Task<HncSdkIpcResult> PingAsync(string sessionId, CancellationToken ct)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult(Fail("Invalid HNC SDK session"));
        // HNC_NetIsConnect 返回 0=已连接，-1=未连接；之前 >=0 的判定把"非连接"也算成 OK。
        var value = CallByArity(session.Api, "HNC_NetIsConnect");
        return Task.FromResult(new HncSdkIpcResult { ReturnCode = Convert.ToInt32(value) == Ok ? Ok : Error });
    }

    public Task<HncSdkValueResult<object>> ReadAsync(HncSdkReadRequest request, CancellationToken ct)
    {
        if (!sessions.TryGetValue(request.SessionId, out var session))
            return Task.FromResult(Value<object>(Error, null, "Invalid HNC SDK session"));

        try
        {
            var value = ReadCore(session.Api, request.Address);
            return Task.FromResult(Value(Ok, value));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Value<object>(Error, null, ex.Message));
        }
    }

    public Task<HncSdkIpcResult> WriteAsync(HncSdkWriteRequest request, CancellationToken ct)
    {
        if (!sessions.TryGetValue(request.SessionId, out var session))
            return Task.FromResult(Fail("Invalid HNC SDK session"));

        try
        {
            WriteCore(session.Api, request.Address, request.DataType, request.Value);
            return Task.FromResult(new HncSdkIpcResult { ReturnCode = Ok });
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex.Message));
        }
    }

    public Task<HncSdkValueResult<IReadOnlyList<HncSdkFileEntry>>> BrowseFilesAsync(HncSdkBrowseRequest request, CancellationToken ct)
    {
        if (!sessions.TryGetValue(request.SessionId, out var session))
            return Task.FromResult(Value<IReadOnlyList<HncSdkFileEntry>>(Error, [], "Invalid HNC SDK session"));
        var lines = new List<string>();
        var remotePath = NormalizeRemotePath(request.Path);
        var ret = HncSdkFtpApi.GetDirInfo(remotePath, session.Host, lines);
        return Task.FromResult(ret == Ok
            ? Value(Ok, HncSdkFileListParser.Parse(remotePath, lines))
            : Value<IReadOnlyList<HncSdkFileEntry>>(ret, [], $"HNC SDK GetDirInfo failed: {ret}"));
    }

    public Task<HncSdkIpcResult> UploadAsync(HncSdkTransferRequest request, CancellationToken ct)
        => TransferAsync(request, upload: true);

    public Task<HncSdkIpcResult> DownloadAsync(HncSdkTransferRequest request, CancellationToken ct)
        => TransferAsync(request, upload: false);

    public Task<HncSdkIpcResult> RemoveAsync(HncSdkRemoveRequest request, CancellationToken ct)
    {
        if (!sessions.TryGetValue(request.SessionId, out var session))
            return Task.FromResult(Fail("Invalid HNC SDK session"));
        var remote = NormalizeRemotePath(request.RemotePath);
        var ret = HncSdkFtpApi.RemoveFile(remote, session.Host);
        return Task.FromResult(ret == Ok
            ? new HncSdkIpcResult { ReturnCode = Ok }
            : new HncSdkIpcResult { ReturnCode = ret, ErrorMessage = $"HNC SDK RemoveFile failed: {ret}" });
    }

    public Task<HncSdkIpcResult> RenameAsync(HncSdkRenameRequest request, CancellationToken ct)
    {
        if (!sessions.TryGetValue(request.SessionId, out var session))
            return Task.FromResult(Fail("Invalid HNC SDK session"));
        var remote = NormalizeRemotePath(request.RemotePath);
        var ret = HncSdkFtpApi.RenameFile(remote, request.NewName, session.Host);
        return Task.FromResult(ret == Ok
            ? new HncSdkIpcResult { ReturnCode = Ok }
            : new HncSdkIpcResult { ReturnCode = ret, ErrorMessage = $"HNC SDK RenameFile failed: {ret}" });
    }

    private static object ReadCore(object api, string address)
    {
        var parts = address.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) throw new InvalidOperationException($"Invalid HNC SDK address: {address}");

        return parts[0].ToLowerInvariant() switch
        {
            "sys" => ReadSys(api, parts),
            "chan" => ReadChan(api, parts),
            "axis" => ReadAxis(api, parts),
            "crds" => ReadCrds(api, parts),
            "var" => ReadVar(api, parts),
            "param" => ReadParam(api, parts),
            "tool" => ReadTool(api, parts),
            "alarm" => ReadAlarm(api, parts),
            "program" => ReadProgram(api, parts),
            _ => throw new InvalidOperationException($"Unsupported HNC SDK address: {address}"),
        };
    }

    private static object ReadSys(object api, string[] parts)
    {
        var type = ResolveEnumOrInt("HNCAPI_INTERFACE.HncSystem", "HNC_SYS_", parts[1]);
        return ReadValue(api, "HNC_SystemGetValue", [type], preferString: true);
    }

    private static object ReadChan(object api, string[] parts)
    {
        if (parts.Length < 4) throw new InvalidOperationException("Chan read needs chan:type:ch:index");
        var type = ResolveEnumOrInt("HNCAPI_INTERFACE.HncChannel", "HNC_CHAN_", parts[1]);
        return ReadValue(api, "HNC_ChannelGetValue", [type, int.Parse(parts[2]), int.Parse(parts[3])], preferString: false);
    }

    private static object ReadAxis(object api, string[] parts)
    {
        if (parts.Length < 3) throw new InvalidOperationException("Axis read needs axis:type:ax");
        var type = ResolveEnumOrInt("HNCAPI_INTERFACE.HncAxis", "HNC_AXIS_", parts[1]);
        return ReadValue(api, "HNC_AxisGetValue", [type, int.Parse(parts[2])], preferString: false);
    }

    private static object ReadCrds(object api, string[] parts)
    {
        if (parts.Length < 5) throw new InvalidOperationException("Crds read needs crds:type:ax:ch:crds");
        var type = ResolveEnumOrInt("HNCAPI_INTERFACE.HncCRDS", "HNC_CRDS_", parts[1]);
        // HNC_CrdsGetValue(type, ax, ref value, ch, crds) — value is the middle parameter
        return ReadValueInMiddle(api, "HNC_CrdsGetValue",
            prefix: [type, int.Parse(parts[2])],
            suffix: [int.Parse(parts[3]), int.Parse(parts[4])]);
    }

    private static object ReadVar(object api, string[] parts)
    {
        if (parts.Length < 4) throw new InvalidOperationException("Var read needs var:type:no:index");
        var type = ResolveEnumOrInt("HNCAPI_INTERFACE.HncVarType", "HNC_VARTYPE_", parts[1]);
        return ReadValue(api, "HNC_VarGetValue", [type, int.Parse(parts[2]), int.Parse(parts[3])], preferString: false);
    }

    private static object ReadParam(object api, string[] parts)
    {
        var id = int.Parse(parts[1]);
        var propType = (byte)(parts.Length > 2 ? int.Parse(parts[2]) : 0);
        return ReadValue(api, "HNC_ParamanGetParaPropEx", [id, propType], preferString: false);
    }

    private static object ReadTool(object api, string[] parts)
    {
        if (parts[1].Equals("max", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt32(CallByArity(api, "HNC_ToolGetMaxToolNum"));

        if (parts.Length < 3) throw new InvalidOperationException("Tool read needs tool:toolNo:index or tool:max");
        return ReadValue(api, "HNC_ToolGetToolPara", [int.Parse(parts[1]), int.Parse(parts[2])], preferString: false);
    }

    private static object ReadAlarm(object api, string[] parts)
    {
        if (parts[1].Equals("count", StringComparison.OrdinalIgnoreCase))
        {
            var numArgs = new object[] { 0 };
            EnsureOk(InvokeWithRefs(api, "HNC_AlarmGetNum", numArgs), "HNC_AlarmGetNum");
            return numArgs[0]!;
        }
        var idx = int.Parse(parts[1]);
        var args = new object[] { idx, 0, "" };
        EnsureOk(InvokeWithRefs(api, "HNC_AlarmGetData", args), "HNC_AlarmGetData");
        return $"[{args[1]}] {args[2]}";
    }

    private static object ReadProgram(object api, string[] parts)
    {
        if (!parts[1].Equals("current", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Program read needs program:current[:ch]");
        var ch = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0;
        var args = new object[] { ch, "" };
        InvokeWithRefs(api, "HNC_FprogGetFullName", args);
        return args[1] ?? "";
    }

    private static object ReadValue(object api, string method, object[] prefix, bool preferString)
    {
        // Try by sample type to pick the matching overload (Int32/Double/String).
        var samples = preferString
            ? new object[] { "", 0, 0d }
            : new object[] { 0d, 0, "" };

        foreach (var sample in samples)
        {
            var args = prefix.Concat([sample]).ToArray();
            if (InvokeWithRefs(api, method, args) == Ok)
                return args[^1] ?? sample;
        }
        throw new InvalidOperationException($"{method} returned non-zero for all overloads (prefix=[{string.Join(",", prefix)}])");
    }

    private static object ReadValueInMiddle(object api, string method, object[] prefix, object[] suffix)
    {
        foreach (var sample in new object[] { 0d, 0 })
        {
            var args = prefix.Concat([sample]).Concat(suffix).ToArray();
            var refIndex = prefix.Length;
            if (InvokeWithRefs(api, method, args) == Ok)
                return args[refIndex] ?? sample;
        }
        throw new InvalidOperationException($"{method} returned non-zero (prefix=[{string.Join(",", prefix)}], suffix=[{string.Join(",", suffix)}])");
    }

    private static void WriteCore(object api, string address, string dataType, string rawValue)
    {
        var parts = address.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) throw new InvalidOperationException($"Invalid HNC SDK write address: {address}");

        switch (parts[0].ToLowerInvariant())
        {
            case "var": WriteVar(api, parts, dataType, rawValue); return;
            case "param": WriteParam(api, parts, dataType, rawValue); return;
            case "crds": WriteCrds(api, parts, dataType, rawValue); return;
            case "tool": WriteTool(api, parts, dataType, rawValue); return;
            default: throw new InvalidOperationException($"Write not supported for HNC SDK address: {address}");
        }
    }

    private static void WriteVar(object api, string[] parts, string dataType, string rawValue)
    {
        if (parts.Length < 4) throw new InvalidOperationException("Var write needs var:type:no:index");
        var type = ResolveEnumOrInt("HNCAPI_INTERFACE.HncVarType", "HNC_VARTYPE_", parts[1]);
        InvokeSet(api, "HNC_VarSetValue", [type, int.Parse(parts[2]), int.Parse(parts[3])], dataType, rawValue);
    }

    private static void WriteParam(object api, string[] parts, string dataType, string rawValue)
    {
        var id = int.Parse(parts[1]);
        var propType = (byte)(parts.Length > 2 ? int.Parse(parts[2]) : 0);
        InvokeSet(api, "HNC_ParamanSetParaPropEx", [id, propType], dataType, rawValue, allowString: true);
    }

    private static void WriteCrds(object api, string[] parts, string dataType, string rawValue)
    {
        if (parts.Length < 5) throw new InvalidOperationException("Crds write needs crds:type:ax:ch:crds");
        var type = ResolveEnumOrInt("HNCAPI_INTERFACE.HncCRDS", "HNC_CRDS_", parts[1]);
        var ax = int.Parse(parts[2]);
        var ch = int.Parse(parts[3]);
        var crds = int.Parse(parts[4]);

        var preferDouble = PreferDouble(dataType);
        foreach (var sample in preferDouble ? new object[] { Convert.ToDouble(rawValue), Convert.ToInt32(Convert.ToDouble(rawValue)) }
                                            : new object[] { Convert.ToInt32(Convert.ToDouble(rawValue)), Convert.ToDouble(rawValue) })
        {
            var args = new[] { type, ax, sample, ch, crds };
            if (InvokeWithRefs(api, "HNC_CrdsSetValue", args) == Ok) return;
        }
        throw new InvalidOperationException($"HNC_CrdsSetValue failed for crds:{parts[1]}:{ax}:{ch}:{crds}");
    }

    private static void WriteTool(object api, string[] parts, string dataType, string rawValue)
    {
        if (parts.Length < 3) throw new InvalidOperationException("Tool write needs tool:toolNo:index");
        InvokeSet(api, "HNC_ToolSetToolPara", [int.Parse(parts[1]), int.Parse(parts[2])], dataType, rawValue);
    }

    private static void InvokeSet(object api, string method, object[] prefix, string dataType, string rawValue, bool allowString = false)
    {
        var preferDouble = PreferDouble(dataType);
        var attempts = new List<object>();
        if (allowString && dataType.Equals("String", StringComparison.OrdinalIgnoreCase))
            attempts.Add(rawValue);
        if (preferDouble) attempts.Add(Convert.ToDouble(rawValue));
        attempts.Add(Convert.ToInt32(Convert.ToDouble(rawValue)));
        if (!preferDouble) attempts.Add(Convert.ToDouble(rawValue));
        if (allowString && !dataType.Equals("String", StringComparison.OrdinalIgnoreCase))
            attempts.Add(rawValue);

        foreach (var sample in attempts)
        {
            var args = prefix.Concat([sample]).ToArray();
            if (InvokeWithRefs(api, method, args) == Ok) return;
        }
        throw new InvalidOperationException($"{method} failed for all overloads (prefix=[{string.Join(",", prefix)}], value={rawValue})");
    }

    private static bool PreferDouble(string dataType) =>
        dataType.Equals("Double", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("Float", StringComparison.OrdinalIgnoreCase);

    private static int ResolveEnumOrInt(string fullTypeName, string prefix, string text)
    {
        if (int.TryParse(text, out var v)) return v;
        return ResolveEnum(fullTypeName, prefix, text);
    }

    private static int ResolveEnum(string fullTypeName, string prefix, string name)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(x => x.GetType(fullTypeName)).FirstOrDefault(x => x is not null)
            ?? throw new InvalidOperationException($"Missing SDK enum {fullTypeName}");
        var fullName = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name : $"{prefix}{name}";
        return Convert.ToInt32(Enum.Parse(type, fullName, ignoreCase: true));
    }

    private static int InvokeWithRefs(object target, string methodName, object[] args)
    {
        var candidates = target.GetType().GetMethods()
            .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
            .ToArray();
        foreach (var method in candidates)
        {
            var parameters = method.GetParameters();
            if (!Enumerable.Range(0, args.Length).All(i => TypeMatches(ElementOf(parameters[i].ParameterType), args[i].GetType())))
                continue;

            var converted = new object?[args.Length];
            var ok = true;
            for (var i = 0; i < args.Length; i++)
            {
                var pt = ElementOf(parameters[i].ParameterType);
                try
                {
                    converted[i] = pt.IsInstanceOfType(args[i]) ? args[i] :
                        pt == typeof(string) ? args[i].ToString() :
                        Convert.ChangeType(args[i], pt);
                }
                catch { ok = false; break; }
            }
            if (!ok) continue;

            try
            {
                var rc = Convert.ToInt32(method.Invoke(target, converted));
                for (var i = 0; i < args.Length; i++)
                    if (parameters[i].ParameterType.IsByRef) args[i] = converted[i]!;
                return rc;
            }
            catch { /* try next overload */ }
        }
        return Error;
    }

    private static Type ElementOf(Type t) => t.IsByRef ? t.GetElementType()! : t;

    private static bool TypeMatches(Type parameterType, Type argType)
    {
        if (parameterType == argType) return true;
        if (parameterType == typeof(string) && argType == typeof(string)) return true;
        if (parameterType == typeof(double) && argType == typeof(double)) return true;
        if (parameterType == typeof(float) && (argType == typeof(double) || argType == typeof(float))) return true;
        if (IsIntegerType(parameterType) && IsIntegerType(argType)) return true;
        return false;
    }

    private static bool IsIntegerType(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

    private Task<HncSdkIpcResult> TransferAsync(HncSdkTransferRequest request, bool upload)
    {
        if (!sessions.TryGetValue(request.SessionId, out var session))
            return Task.FromResult(Fail("Invalid HNC SDK session"));

        var remotePath = NormalizeRemotePath(request.RemotePath);
        var ret = upload
            ? HncSdkFtpApi.UploadFile(remotePath, request.LocalPath, session.Host)
            : HncSdkFtpApi.DownloadFile(remotePath, request.LocalPath, session.Host);
        return Task.FromResult(ret == Ok
            ? new HncSdkIpcResult { ReturnCode = Ok }
            : new HncSdkIpcResult { ReturnCode = ret, ErrorMessage = $"HNC SDK FTP transfer failed: {ret}" });
    }

    private static string NormalizeRemotePath(string? path)
        => string.IsNullOrWhiteSpace(path) || path == "/" ? "" : path.Replace('\\', '/').TrimStart('/');

    private static object? CallByArity(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethods().First(m => m.Name == methodName && m.GetParameters().Length == args.Length);
        return method.Invoke(target, args);
    }

    private static object? Call(object target, string methodName, params object[] args)
        => target.GetType().GetMethod(methodName, args.Select(x => x.GetType()).ToArray())!.Invoke(target, args);

    private static void TryCall(object target, string methodName, params object[] args)
    {
        try
        {
            var method = target.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
            method?.Invoke(target, args);
        }
        catch { }
    }

    private void TryStaticCall(string methodName, params object[] args)
    {
        if (hncApiType is null) return;
        try
        {
            var method = hncApiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
            method?.Invoke(null, args);
        }
        catch { }
    }

    private static void EnsureOk(int rc, string name)
    {
        if (rc != Ok) throw new InvalidOperationException($"{name} failed: {rc}");
    }

    private static HncSdkConnectResult FailConnect(int rc, string message)
        => new() { ReturnCode = rc, ErrorMessage = message };
    private static HncSdkIpcResult Fail(string message)
        => new() { ReturnCode = Error, ErrorMessage = message };
    private static HncSdkValueResult<T> Value<T>(int rc, T? value, string? message = null)
        => new() { ReturnCode = rc, Value = value, ErrorMessage = message };

    private sealed record HncSdkSession(object Api, string Host);
}
