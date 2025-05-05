using System;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace OpenUtau.Core.Controllers {
    [ApiController]
    [Route("api/[controller]")]
    public class ProjectController : ControllerBase {
        [HttpGet]
        public IActionResult Get() {
            Console.WriteLine("Received GET request for project");
            return Ok(new {
                status = "ok",
                message = "OpenUtau HTTP API is running"
            });
        }
    }
} 
