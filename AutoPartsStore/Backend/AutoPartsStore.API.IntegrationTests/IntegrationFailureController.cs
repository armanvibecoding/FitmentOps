using Microsoft.AspNetCore.Mvc;

namespace AutoPartsStore.API.IntegrationTests;

[ApiController]
[Route("integration-test")]
public sealed class IntegrationFailureController : ControllerBase
{
    [HttpGet("throw")]
    public IActionResult Throw() =>
        throw new InvalidOperationException("integration-sensitive-exception-detail");
}
