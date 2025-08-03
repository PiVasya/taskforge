using Microsoft.AspNetCore.Mvc;

namespace testapplication.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SumController : ControllerBase
    {
        [HttpGet]
        public int Get(int a, int b)
        {
            Console.WriteLine("сумма шо-то там");
            return a + b;
        }
    }
}
