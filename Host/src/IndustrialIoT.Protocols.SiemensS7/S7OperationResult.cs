namespace IndustrialIoT.Protocols.SiemensS7;

internal sealed record S7OperationResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    public static S7OperationResult Ok() => new() { Success = true };
    public static S7OperationResult Fail(string error) => new() { Success = false, ErrorMessage = error };
}

internal sealed record S7OperationResult<T>
{
    public required bool Success { get; init; }
    public T? Value { get; init; }
    public string? ErrorMessage { get; init; }

    public static S7OperationResult<T> Ok(T value) => new() { Success = true, Value = value };
    public static S7OperationResult<T> Fail(string error) => new() { Success = false, ErrorMessage = error };
}
