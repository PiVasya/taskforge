using Microsoft.AspNetCore.Mvc;
using testapplication.Models;

namespace testapplication.Controllers
{
    [ApiController]
    [Route("math")]
    public class MathController : ControllerBase
    {
        
        [HttpPost("multiply")]
        public IActionResult Multiply([FromBody] MultiplyRequest request)
        {
            Console.WriteLine("Математика шо-то там");
            return Ok(request.X * request.Y);
        }
    }
}
