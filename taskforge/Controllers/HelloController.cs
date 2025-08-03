using Microsoft.AspNetCore.Mvc;

namespace testapplication.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class HelloController : ControllerBase
    {
        [HttpGet]
        public string Get()
        {
            Console.WriteLine("Hello World");
            return "Hello, world!";
        }
    }
}
