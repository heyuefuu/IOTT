using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace MachineConnectionApi.Controllers;

[ApiController]
[Route("api/verify")]
public sealed class VerifyController : ControllerBase
{
    private readonly IVerifyAutomationService _service;

    public VerifyController(IVerifyAutomationService service) => _service = service;

    [HttpPost("run")]
    public async Task<ActionResult<VerifyRunResponse>> Run(
        [FromBody] VerifyRunRequest request,
        CancellationToken ct) =>
        Ok(await _service.RunAsync(request, ct));
}
