using Microsoft.AspNetCore.Mvc;
using FileUploader.Data;

namespace FileUploader.Controllers;

[ApiController]
[Route("api")]
public class HealthController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<HealthController> _logger;

    public HealthController(ApplicationDbContext dbContext, ILogger<HealthController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            // Check database connectivity
            var canConnect = await _dbContext.Database.CanConnectAsync();

            if (!canConnect)
            {
                _logger.LogError("Health check failed: Cannot connect to database");
                return StatusCode(503, new
                {
                    status = "unhealthy",
                    message = "Database connection failed"
                });
            }

            return Ok(new
            {
                status = "healthy",
                database = "connected"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed with exception");
            return StatusCode(503, new
            {
                status = "unhealthy",
                message = "Internal error"
            });
        }
    }

    [HttpGet("health/ready")]
    public async Task<IActionResult> Ready()
    {
        try
        {
            // Check if database is ready for queries
            var canConnect = await _dbContext.Database.CanConnectAsync();

            if (!canConnect)
            {
                return StatusCode(503, new { status = "not ready" });
            }

            return Ok(new { status = "ready" });
        }
        catch
        {
            return StatusCode(503, new { status = "not ready" });
        }
    }

    [HttpGet("health/live")]
    public IActionResult Live()
    {
        // Simple liveness check - app is running
        return Ok(new
        {
            status = "alive"
        });
    }
}