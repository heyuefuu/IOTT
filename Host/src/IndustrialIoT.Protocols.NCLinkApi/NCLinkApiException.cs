namespace IndustrialIoT.Protocols.NCLinkApi;

public class NCLinkApiException : Exception
{
    public NCLinkApiStatusCode StatusCode { get; }
    public string? Status { get; }

    public NCLinkApiException(string message) : base(message)
    {
        StatusCode = NCLinkApiStatusCode.RequestErrOther;
    }

    public NCLinkApiException(NCLinkApiStatusCode code, string? status = null, string? detail = null)
        : base(detail is null
            ? $"NC-Link API {status ?? "FAILED"}: code={(int)code} ({code.Describe()})"
            : $"NC-Link API {status ?? "FAILED"}: code={(int)code} ({code.Describe()}) — {detail}")
    {
        StatusCode = code;
        Status = status;
    }

    public NCLinkApiException(string message, Exception inner) : base(message, inner)
    {
        StatusCode = NCLinkApiStatusCode.RequestErrOther;
    }
}
