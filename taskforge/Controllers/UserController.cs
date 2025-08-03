using Microsoft.AspNetCore.Mvc;
using testapplication.Models;

namespace testapplication.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class UserController : ControllerBase
    {
        [HttpPost]
        public IActionResult CreateUser([FromBody] UserDto user)
        {
            Console.WriteLine("Какой-то пользователь");
            return Ok($"Принят пользователь: {user.Name}, возраст {user.Age}");
        }
    }
}
