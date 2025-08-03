using Microsoft.AspNetCore.Mvc;
using testapplication.Models;

namespace testapplication.Controllers
{
    [ApiController]
    [Route("text")]
    public class TextController : ControllerBase
    {
        [HttpPut("upper")]
        public IActionResult Uppercase([FromBody] TextRequest request)
        {
            Console.WriteLine("текст");
            return Ok(request.Text.ToUpper());
        }
    }
}
